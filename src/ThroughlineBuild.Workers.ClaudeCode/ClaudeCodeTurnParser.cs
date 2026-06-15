using System.Text.Json;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Behavior-inert per-turn context-attribution parser. Runs AFTER the claude-code worker exits,
// over the already-captured NDJSON stdout stream (a string already in memory). No flag, no prompt
// change, no process spawn - pure post-exit arithmetic.
//
// It reconstructs the per-turn token series (cache_read / cache_creation / output) and attributes
// each turn's cache_creation to a single tool class, so the byte buckets form a partition of total
// cache_creation. This is the structured input for downstream context-hygiene analysis.
//
// AOT-safe by construction: parses with JsonDocument and manual element reads only. No
// reflection-based JsonSerializer, no source-gen context to keep in sync. Best-effort per line: a
// malformed line is swallowed and skipped.
//
// Stack-agnostic: it observes only claude-code vendor tool NAMES (Read/Write/TodoWrite/Task/Bash/
// ...) and token counts. No file-extension, language, or framework knowledge lives here.
internal static class ClaudeCodeTurnParser
{
    // Canonical usage-shape reader. WorkerTranscriptWriter.ReadUsage delegates here so the field
    // names live in exactly one place. Each field is Number -> Int64, else 0.
    internal static (long Input, long Output, long CacheRead, long CacheCreation) ReadUsage(JsonElement usageEl)
    {
        return (
            LongOrZero(usageEl, "input_tokens"),
            LongOrZero(usageEl, "output_tokens"),
            LongOrZero(usageEl, "cache_read_input_tokens"),
            LongOrZero(usageEl, "cache_creation_input_tokens"));
    }

    private static long LongOrZero(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0L;

    // ---- tool-class sets (Ordinal, vendor tool names only) ----

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "Read", "Grep", "Glob", "NotebookRead", "LS"
    };

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit"
    };

    private static readonly HashSet<string> TodoTools = new(StringComparer.Ordinal)
    {
        "TodoWrite"
    };

    private static readonly HashSet<string> TaskTools = new(StringComparer.Ordinal)
    {
        "Task",  // pre-rename sub-agent tool name (<= 2.1.52 transcripts)
        "Agent"  // current sub-agent tool name (2.1.177)
    };

    private static readonly HashSet<string> BashTools = new(StringComparer.Ordinal)
    {
        "Bash"
    };

    // Classifies a single tool name into one of the six classes. Anything unrecognized -> "other".
    internal static string ClassifyTool(string toolName)
    {
        if (WriteTools.Contains(toolName)) return "write";
        if (TaskTools.Contains(toolName)) return "task";
        if (TodoTools.Contains(toolName)) return "todo";
        if (ReadTools.Contains(toolName)) return "read";
        if (BashTools.Contains(toolName)) return "bash";
        return "other";
    }

    // Selects ONE class for a turn that may issue several tool_use blocks, by precedence
    // write > task > todo > read > bash > other. Mirrors the single-class-per-turn precedence in
    // WorkerTranscriptWriter.ClassifyTurn (write wins) so the per-class byte buckets stay a partition
    // of total cache_creation. A turn with zero tool_use blocks -> "other".
    internal static string SelectTurnClass(IEnumerable<string> toolNamesInTurn)
    {
        bool anyTask = false, anyTodo = false, anyRead = false, anyBash = false;
        foreach (var name in toolNamesInTurn)
        {
            switch (ClassifyTool(name))
            {
                case "write": return "write"; // highest precedence: short-circuit
                case "task": anyTask = true; break;
                case "todo": anyTodo = true; break;
                case "read": anyRead = true; break;
                case "bash": anyBash = true; break;
            }
        }
        if (anyTask) return "task";
        if (anyTodo) return "todo";
        if (anyRead) return "read";
        if (anyBash) return "bash";
        return "other";
    }

    // Ratio of the late-window average to the early-window average of the cache_read series, a
    // crude proxy for context-growth trajectory. Returns -1.0 (sentinel: not enough data / undefined)
    // when there are fewer than 8 turns or the early average is zero.
    internal static double ComputeSlopeRatio(IReadOnlyList<long> cacheReadSeries)
    {
        if (cacheReadSeries.Count < 8) return -1.0;
        double firstAvg = (cacheReadSeries[0] + cacheReadSeries[1] + cacheReadSeries[2]) / 3.0;
        if (firstAvg == 0) return -1.0;
        int n = cacheReadSeries.Count;
        double lastSum = cacheReadSeries[n - 1] + cacheReadSeries[n - 2] + cacheReadSeries[n - 3]
                       + cacheReadSeries[n - 4] + cacheReadSeries[n - 5];
        double lastAvg = lastSum / 5.0;
        return lastAvg / firstAvg;
    }

    // Parses the captured NDJSON stdout into a per-turn context-attribution series. Only
    // type=="assistant" events carry turns. An assistant message is emitted as several NDJSON lines
    // sharing one message.id; each DISTINCT message is one turn (deduped by id, flushing on id change,
    // exactly like WorkerTranscriptWriter.AccumulateAssistant). Usage is captured once per message
    // (it repeats identically across the message's lines).
    internal static ContextTurnSeries Parse(string stdout)
    {
        var cacheReadSeries = new List<long>();
        var cacheCreationSeries = new List<long>();
        var outputSeries = new List<long>();
        long readBytes = 0, writeBytes = 0, todoBytes = 0, taskBytes = 0, bashBytes = 0, otherBytes = 0;

        // Current-turn accumulation state.
        bool curHasContent = false;
        string? curMsgId = null;
        long curCacheRead = 0, curCacheCreation = 0, curOutput = 0;
        var curToolNames = new List<string>();

        void FlushTurn()
        {
            if (!curHasContent) return;
            cacheReadSeries.Add(curCacheRead);
            cacheCreationSeries.Add(curCacheCreation);
            outputSeries.Add(curOutput);

            var cls = SelectTurnClass(curToolNames);
            switch (cls)
            {
                case "read": readBytes += curCacheCreation; break;
                case "write": writeBytes += curCacheCreation; break;
                case "todo": todoBytes += curCacheCreation; break;
                case "task": taskBytes += curCacheCreation; break;
                case "bash": bashBytes += curCacheCreation; break;
                default: otherBytes += curCacheCreation; break;
            }

            // reset
            curHasContent = false;
            curMsgId = null;
            curCacheRead = 0;
            curCacheCreation = 0;
            curOutput = 0;
            curToolNames = new List<string>();
        }

        if (!string.IsNullOrEmpty(stdout))
        {
            foreach (var rawLine in stdout.Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var typeEl)
                        || typeEl.ValueKind != JsonValueKind.String
                        || typeEl.GetString() != "assistant")
                        continue;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                        continue;

                    var msgId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString()
                        : null;

                    // A new message id means a new turn: flush the prior one first.
                    if (curHasContent && !string.Equals(msgId, curMsgId, StringComparison.Ordinal))
                        FlushTurn();

                    if (!curHasContent)
                        curMsgId = msgId;
                    curHasContent = true;

                    // message.usage is identical across the lines of one message; capture it once.
                    if (msg.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        var (_, output, cacheRead, cacheCreation) = ReadUsage(usageEl);
                        curCacheRead = cacheRead;
                        curCacheCreation = cacheCreation;
                        curOutput = output;
                    }

                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object) continue;
                            if (!block.TryGetProperty("type", out var btEl)
                                || btEl.ValueKind != JsonValueKind.String
                                || btEl.GetString() != "tool_use")
                                continue;
                            var name = block.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                                ? nameEl.GetString()
                                : null;
                            if (name is not null) curToolNames.Add(name);
                        }
                    }
                }
            }
        }

        // Flush the final dangling turn at end-of-stream.
        FlushTurn();

        long totalCacheRead = 0;
        foreach (var v in cacheReadSeries) totalCacheRead += v;

        return new ContextTurnSeries(
            cacheReadSeries,
            cacheCreationSeries,
            outputSeries,
            cacheReadSeries.Count,
            readBytes,
            writeBytes,
            todoBytes,
            taskBytes,
            bashBytes,
            otherBytes,
            totalCacheRead,
            ComputeSlopeRatio(cacheReadSeries));
    }
}

// Per-turn context-attribution summary. The three series are parallel (one entry per turn).
// The *_bytes fields attribute each turn's cache_creation to a single tool class, forming a
// partition of total cache_creation. SlopeRatio is -1.0 when undefined (fewer than 8 turns or a
// zero early window).
internal sealed record ContextTurnSeries(
    IReadOnlyList<long> CacheReadSeries,
    IReadOnlyList<long> CacheCreationSeries,
    IReadOnlyList<long> OutputSeries,
    int Turns,
    long ReadBytes,
    long WriteBytes,
    long TodoBytes,
    long TaskBytes,
    long BashBytes,
    long OtherBytes,
    long TotalCacheRead,
    double SlopeRatio);
