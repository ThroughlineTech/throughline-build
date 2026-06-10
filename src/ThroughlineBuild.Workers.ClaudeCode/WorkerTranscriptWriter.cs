using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Writes a stable, mechanically-comparable JSONL transcript of one claude-code worker
// session into the --debug capture directory, alongside the raw worker-stdin/stdout/stderr
// files. This is the structured counterpart to the raw firehose: claude-code already emits
// per-turn stream-json containing nearly everything analysis needs, but it is parsed for
// WORKER_RESULT and otherwise discarded. Under --debug we re-emit it in a stable schema so
// cross-run comparison (turn counts, per-turn context size, redundant-read rate, files
// read-vs-named) is a jq away rather than a bespoke parse per run.
//
// HARD CONSTRAINT: pure observation. This runs AFTER the worker exits, reads only the
// captured stream + brief, and writes only to disk. It never touches the worker's prompt or
// behavior - the worker runs identically with or without --debug.
//
// Schema: one JSON object per line, discriminated by "rec":
//   meta        - line 1: keying + the exact prompt's identity + the brief's named files.
//   turn        - one per assistant message (grouped by message id; an assistant message is
//                 emitted as several NDJSON lines sharing one id). Carries per-turn usage
//                 (input/output/cache_read/cache_creation), the tool calls verbatim, a
//                 derived discovery/production/verification class, and per-turn latency.
//   tool_result - one per tool_result returned to the model: size (bytes/lines) + error flag.
//   result      - terminal: cumulative usage, cost, num_turns, and the files-read / -written
//                 / -changed sets for diffing against the brief's named files.
//
// AOT-safe: parses with JsonDocument and re-emits with Utf8JsonWriter. No reflection-based
// serialization, no source-gen context to keep in sync. Best-effort end to end: a malformed
// line is counted and skipped, and any failure is swallowed so debug capture never changes
// dispatch behavior.
internal static class WorkerTranscriptWriter
{
    internal const int SchemaVersion = 1;
    internal const string FileName = "transcript.jsonl";

    // Read-class tools whose targets are concrete files we count as "read" (for redundant-read
    // analysis). Search tools (Glob/Grep) and Bash are NOT reads - they are recorded in the
    // verbatim tool list but do not enter files_read.
    private static readonly HashSet<string> ReadFileTools = new(StringComparer.Ordinal)
    {
        "Read", "NotebookRead"
    };

    private static readonly HashSet<string> WriteFileTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit"
    };

    private static readonly HashSet<string> DiscoveryTools = new(StringComparer.Ordinal)
    {
        "Read", "NotebookRead", "Glob", "Grep", "LS", "WebFetch", "WebSearch", "Task"
    };

    // Best-effort entry point: build the transcript and write it to <captureDir>/transcript.jsonl.
    // No-op friendly: swallows every failure so debug capture never masks or alters the run.
    public static void Write(
        string captureDir,
        Brief brief,
        IReadOnlyList<(DateTimeOffset At, string Line)> timestampedLines,
        WorkerResult result,
        DebugTranscriptContext? context,
        string? model,
        IReadOnlyList<string> invocationArgs,
        long wallClockMs,
        DateTimeOffset startedAt)
    {
        try
        {
            var jsonl = BuildJsonl(brief, timestampedLines, result, context, model, invocationArgs, wallClockMs, startedAt);
            Directory.CreateDirectory(captureDir);
            File.WriteAllText(Path.Combine(captureDir, FileName), jsonl, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Best-effort: a transcript-write failure must never change phase behavior.
        }
    }

    // Pure core: turns the captured stream into the JSONL text. Extracted from Write so it can
    // be unit-tested without touching the filesystem.
    internal static string BuildJsonl(
        Brief brief,
        IReadOnlyList<(DateTimeOffset At, string Line)> timestampedLines,
        WorkerResult result,
        DebugTranscriptContext? context,
        string? model,
        IReadOnlyList<string> invocationArgs,
        long wallClockMs,
        DateTimeOffset startedAt)
    {
        var lines = new List<string>();

        // System-event fields (captured as we encounter the init event).
        string? sessionTools = null;        // raw JSON array text of available tools
        string? claudeCodeVersion = null;
        string? cwd = null;
        string? streamSessionId = null;

        // Turn accumulation state.
        string? curMsgId = null;
        DateTimeOffset curTurnAt = startedAt;
        var curTools = new List<(string Name, string InputJson)>();
        int curTextChars = 0;
        int curThinkingChars = 0;
        UsageTokens curUsage = default;
        bool curHasContent = false;

        int turnIndex = 0;
        DateTimeOffset lastTurnAt = startedAt;
        int skipped = 0;

        // File accumulators (ordered, deduped).
        var filesRead = new OrderedSet();
        var filesWritten = new OrderedSet();

        var turnRecords = new List<string>();
        var bodyRecords = new List<string>();   // turns + tool_results, in stream order

        void FlushTurn()
        {
            if (!curHasContent) return;
            var dtMs = (long)Math.Max(0, (curTurnAt - lastTurnAt).TotalMilliseconds);
            bodyRecords.Add(WriteTurnRecord(turnIndex, curMsgId, curTurnAt, dtMs, curUsage, curTools, curTextChars, curThinkingChars));
            lastTurnAt = curTurnAt;
            turnIndex++;
            // reset
            curMsgId = null;
            curTools = new List<(string, string)>();
            curTextChars = 0;
            curThinkingChars = 0;
            curUsage = default;
            curHasContent = false;
        }

        foreach (var (at, rawLine) in timestampedLines)
        {
            var trimmed = rawLine?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(trimmed); }
            catch (JsonException) { skipped++; continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                { skipped++; continue; }

                switch (typeEl.GetString())
                {
                    case "system":
                        streamSessionId = TryGetString(root, "session_id");
                        claudeCodeVersion = TryGetString(root, "claude_code_version");
                        cwd = TryGetString(root, "cwd");
                        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
                            sessionTools = toolsEl.GetRawText();
                        break;

                    case "assistant":
                        AccumulateAssistant(root, at, ref curMsgId, ref curTurnAt, ref curHasContent,
                            ref curUsage, ref curTextChars, ref curThinkingChars,
                            curTools, filesRead, filesWritten, FlushTurn);
                        break;

                    case "user":
                        // A tool_result closes the current turn (it arrives after the turn that
                        // issued the call), so flush before recording the result.
                        FlushTurn();
                        EmitToolResults(root, at, bodyRecords);
                        break;

                    case "result":
                        FlushTurn();
                        bodyRecords.Add(WriteResultRecord(root, at, result, wallClockMs, filesRead, filesWritten, skipped));
                        break;

                    default:
                        // rate_limit_event and any unknown decoration: ignored.
                        break;
                }
            }
        }

        // Stream ended without a terminal result event (truncated / killed worker): flush any
        // dangling turn and synthesize a result record from the WorkerResult so the file is
        // still self-describing.
        FlushTurn();
        bool hasResultRec = bodyRecords.Exists(r => r.Contains("\"rec\":\"result\""));
        if (!hasResultRec)
            bodyRecords.Add(WriteSyntheticResultRecord(result, wallClockMs, filesRead, filesWritten, skipped));

        lines.Add(WriteMetaRecord(brief, context, model, invocationArgs, startedAt,
            streamSessionId, claudeCodeVersion, cwd, sessionTools));
        lines.AddRange(bodyRecords);
        // turnRecords kept for symmetry; turns already interleaved in bodyRecords.
        _ = turnRecords;

        return string.Join("\n", lines) + "\n";
    }

    private static void AccumulateAssistant(
        JsonElement root,
        DateTimeOffset at,
        ref string? curMsgId,
        ref DateTimeOffset curTurnAt,
        ref bool curHasContent,
        ref UsageTokens curUsage,
        ref int curTextChars,
        ref int curThinkingChars,
        List<(string Name, string InputJson)> curTools,
        OrderedSet filesRead,
        OrderedSet filesWritten,
        Action flushTurn)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return;

        var msgId = TryGetString(msg, "id");
        // A new message id means a new turn: flush the prior one first.
        if (curHasContent && !string.Equals(msgId, curMsgId, StringComparison.Ordinal))
            flushTurn();

        if (!curHasContent)
        {
            curMsgId = msgId;
            curTurnAt = at;
        }
        curHasContent = true;

        // message.usage is identical across the lines of one message; capture it.
        if (msg.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            curUsage = ReadUsage(usageEl);

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var blockType = TryGetString(block, "type");
                switch (blockType)
                {
                    case "tool_use":
                        var name = TryGetString(block, "name") ?? "?";
                        var inputJson = block.TryGetProperty("input", out var inputEl)
                            ? inputEl.GetRawText()
                            : "null";
                        curTools.Add((name, inputJson));
                        CollectFiles(name, block, filesRead, filesWritten);
                        break;
                    case "text":
                        curTextChars += (TryGetString(block, "text") ?? "").Length;
                        break;
                    case "thinking":
                        curThinkingChars += (TryGetString(block, "thinking") ?? "").Length;
                        break;
                }
            }
        }
    }

    private static void CollectFiles(string toolName, JsonElement block, OrderedSet filesRead, OrderedSet filesWritten)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return;
        var path = TryGetString(input, "file_path") ?? TryGetString(input, "notebook_path");
        if (string.IsNullOrEmpty(path)) return;
        if (ReadFileTools.Contains(toolName)) filesRead.Add(path!);
        else if (WriteFileTools.Contains(toolName)) filesWritten.Add(path!);
    }

    private static void EmitToolResults(JsonElement root, DateTimeOffset at, List<string> body)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (TryGetString(block, "type") != "tool_result") continue;
            var toolUseId = TryGetString(block, "tool_use_id");
            var isError = block.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True;
            var (bytes, linesCount) = MeasureToolResultContent(block);
            body.Add(WriteToolResultRecord(toolUseId, at, bytes, linesCount, isError));
        }
    }

    // Tool-result content is usually a string; occasionally a structured array/object. Measure
    // bytes (UTF-8) and newline-delimited line count from the string form either way.
    private static (int Bytes, int Lines) MeasureToolResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var contentEl)) return (0, 0);
        string text = contentEl.ValueKind == JsonValueKind.String
            ? (contentEl.GetString() ?? "")
            : contentEl.GetRawText();
        if (text.Length == 0) return (0, 0);
        int bytes = Encoding.UTF8.GetByteCount(text);
        int newlines = 0;
        foreach (var c in text) if (c == '\n') newlines++;
        int lineCount = newlines + (text.EndsWith('\n') ? 0 : 1);
        return (bytes, lineCount);
    }

    // ---- record writers (one JSON object each) ----

    private static string WriteMetaRecord(
        Brief brief,
        DebugTranscriptContext? ctx,
        string? model,
        IReadOnlyList<string> invocationArgs,
        DateTimeOffset startedAt,
        string? streamSessionId,
        string? claudeCodeVersion,
        string? cwd,
        string? sessionToolsRawJson)
        => WriteObject(w =>
        {
            w.WriteString("rec", "meta");
            w.WriteNumber("schema", SchemaVersion);
            WriteOptionalString(w, "session_id", ctx?.SessionId ?? streamSessionId);
            WriteOptionalString(w, "stream_session_id", streamSessionId);
            w.WriteString("ticket", brief.TicketId);
            w.WriteString("phase", brief.Phase.ToString());
            if (ctx?.ReworkRound is int rr) w.WriteNumber("rework_round", rr);
            WriteOptionalString(w, "build_version", ctx?.BuildVersion);
            WriteOptionalString(w, "model", model);
            WriteOptionalString(w, "claude_code_version", claudeCodeVersion);
            WriteOptionalString(w, "cwd", cwd);

            // The exact prompt the worker received lives verbatim in worker-stdin.txt (written
            // by the agent's raw debug capture); reference it and pin its identity here.
            w.WriteString("prompt_file", "worker-stdin.txt");
            w.WriteNumber("prompt_chars", brief.Instruction.Length);
            w.WriteString("prompt_sha256", Sha256Hex(brief.Instruction));

            WriteStringArray(w, "brief_named_files", brief.RelevantFiles);
            WriteStringArray(w, "brief_named_writes", brief.AllowedWrites);
            WriteStringArray(w, "invocation_args", invocationArgs);
            if (sessionToolsRawJson is not null)
            {
                w.WritePropertyName("session_tools");
                using var d = JsonDocument.Parse(sessionToolsRawJson);
                d.RootElement.WriteTo(w);
            }
            w.WriteString("started_at", startedAt.ToString("O"));
        });

    private static string WriteTurnRecord(
        int index,
        string? messageId,
        DateTimeOffset at,
        long dtMs,
        UsageTokens usage,
        List<(string Name, string InputJson)> tools,
        int textChars,
        int thinkingChars)
        => WriteObject(w =>
        {
            w.WriteString("rec", "turn");
            w.WriteNumber("i", index);
            WriteOptionalString(w, "message_id", messageId);
            w.WriteString("at", at.ToString("O"));
            w.WriteNumber("dt_ms", dtMs);
            w.WriteString("class", ClassifyTurn(tools, textChars, thinkingChars));

            w.WritePropertyName("usage");
            w.WriteStartObject();
            w.WriteNumber("input", usage.Input);
            w.WriteNumber("output", usage.Output);
            w.WriteNumber("cache_read", usage.CacheRead);
            w.WriteNumber("cache_creation", usage.CacheCreation);
            w.WriteEndObject();

            w.WriteNumber("tool_count", tools.Count);
            w.WritePropertyName("tools");
            w.WriteStartArray();
            foreach (var (name, inputJson) in tools)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WritePropertyName("input");
                using var d = JsonDocument.Parse(inputJson);
                d.RootElement.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteNumber("text_chars", textChars);
            w.WriteNumber("thinking_chars", thinkingChars);
        });

    private static string WriteToolResultRecord(string? toolUseId, DateTimeOffset at, int bytes, int lines, bool isError)
        => WriteObject(w =>
        {
            w.WriteString("rec", "tool_result");
            WriteOptionalString(w, "for", toolUseId);
            w.WriteString("at", at.ToString("O"));
            w.WriteNumber("bytes", bytes);
            w.WriteNumber("lines", lines);
            w.WriteBoolean("is_error", isError);
        });

    private static string WriteResultRecord(
        JsonElement root,
        DateTimeOffset at,
        WorkerResult result,
        long wallClockMs,
        OrderedSet filesRead,
        OrderedSet filesWritten,
        int skipped)
        => WriteObject(w =>
        {
            var isError = root.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True;
            w.WriteString("rec", "result");
            w.WriteString("at", at.ToString("O"));
            w.WriteString("status", isError ? "err" : "ok");
            w.WriteString("worker_status", result.Status.ToString());
            WriteIntFromJson(w, "num_turns", root, "num_turns");
            WriteIntFromJson(w, "duration_ms", root, "duration_ms");
            WriteIntFromJson(w, "duration_api_ms", root, "duration_api_ms");
            w.WriteNumber("wall_clock_ms", wallClockMs);
            if (root.TryGetProperty("total_cost_usd", out var costEl) && costEl.ValueKind == JsonValueKind.Number)
                w.WriteNumber("cost_usd", costEl.GetDouble());

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                var u = ReadUsage(usageEl);
                w.WritePropertyName("usage");
                w.WriteStartObject();
                w.WriteNumber("input", u.Input);
                w.WriteNumber("output", u.Output);
                w.WriteNumber("cache_read", u.CacheRead);
                w.WriteNumber("cache_creation", u.CacheCreation);
                w.WriteEndObject();
            }

            WriteFileSets(w, result, filesRead, filesWritten);
            w.WriteNumber("skipped_lines", skipped);
        });

    private static string WriteSyntheticResultRecord(
        WorkerResult result, long wallClockMs, OrderedSet filesRead, OrderedSet filesWritten, int skipped)
        => WriteObject(w =>
        {
            w.WriteString("rec", "result");
            w.WriteString("status", "incomplete");
            w.WriteString("worker_status", result.Status.ToString());
            w.WriteNumber("wall_clock_ms", wallClockMs);
            WriteFileSets(w, result, filesRead, filesWritten);
            w.WriteNumber("skipped_lines", skipped);
        });

    private static void WriteFileSets(Utf8JsonWriter w, WorkerResult result, OrderedSet filesRead, OrderedSet filesWritten)
    {
        WriteStringArray(w, "files_read", filesRead.Items);
        WriteStringArray(w, "files_written", filesWritten.Items);
        WriteStringArray(w, "files_changed", result.FilesChanged);
    }

    // ---- classification ----

    internal static string ClassifyTurn(List<(string Name, string InputJson)> tools, int textChars, int thinkingChars)
    {
        if (tools.Count == 0)
            return thinkingChars > 0 && textChars == 0 ? "reason" : "respond";

        bool anyWrite = false, anyBash = false, anyDiscovery = false;
        foreach (var (name, _) in tools)
        {
            if (WriteFileTools.Contains(name)) anyWrite = true;
            else if (name == "Bash") anyBash = true;
            else if (DiscoveryTools.Contains(name)) anyDiscovery = true;
        }
        if (anyWrite) return "production";
        if (anyBash) return "verification";
        if (anyDiscovery) return "discovery";
        return "tool";
    }

    // ---- low-level helpers ----

    private readonly struct UsageTokens
    {
        public long Input { get; init; }
        public long Output { get; init; }
        public long CacheRead { get; init; }
        public long CacheCreation { get; init; }
    }

    // Delegates to ClaudeCodeTurnParser.ReadUsage so the usage field-name knowledge lives in
    // exactly one place. Byte-output-preserving: maps the returned tuple into UsageTokens unchanged.
    private static UsageTokens ReadUsage(JsonElement usage)
    {
        var (input, output, cacheRead, cacheCreation) = ClaudeCodeTurnParser.ReadUsage(usage);
        return new UsageTokens { Input = input, Output = output, CacheRead = cacheRead, CacheCreation = cacheCreation };
    }

    private static long LongOrZero(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static void WriteIntFromJson(Utf8JsonWriter w, string outName, JsonElement root, string srcName)
    {
        if (root.TryGetProperty(srcName, out var v) && v.ValueKind == JsonValueKind.Number)
            w.WriteNumber(outName, v.GetInt64());
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static void WriteOptionalString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null) w.WriteString(name, value);
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    private static string WriteObject(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }

    // Order-preserving, deduplicating string set for files-read / files-written.
    private sealed class OrderedSet
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _items = new();
        public IReadOnlyList<string> Items => _items;
        public void Add(string value)
        {
            if (_seen.Add(value)) _items.Add(value);
        }
    }
}
