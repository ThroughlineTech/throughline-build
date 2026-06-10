using System.Globalization;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Instance class that converts a single parsed NDJSON stream-event line from the
// Claude Code CLI into a one-line human-readable digest, or returns null when
// the event is uninteresting (rate_limit_event, unknown system subtype, ...).
//
// Output format:
//   [m:ss] kind      payload
// where:
//   m:ss     is the wall-clock offset from worker start (tracked via ResetStart)
//   kind     is one of: system, thinking, assistant, tool_use, result (left-padded to 10)
//   payload  is a short summary, with paths/args truncated to 80 chars
//
// System events are filtered by subtype: "init" renders the session/model line,
// "thinking_tokens" renders a throttled thinking ticker (the CLI emits one every
// few seconds while the model thinks), everything else is dropped.
//
// The public FormatLine(string) implements IWorkerProgressDigester and is best-effort:
// it will not throw on malformed input. The internal overloads are used by tests.
public sealed class ClaudeCodeProgressDigester : IWorkerProgressDigester
{
    internal const int MaxPayloadChars = 120;

    // Emit a thinking ticker line only once per this many estimated thinking
    // tokens, so a long think reads as a slow ticker instead of a flood.
    internal const long ThinkingTokensEmitStep = 5000;

    private DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private long _lastThinkingTokens;

    public void ResetStart()
    {
        _startTime = DateTimeOffset.UtcNow;
        _lastThinkingTokens = 0;
    }

    // IWorkerProgressDigester - best-effort, never throws.
    public string? FormatLine(string rawNdjsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawNdjsonLine);
            return FormatLine(doc.RootElement, _startTime);
        }
        catch { return null; }
    }

    // Format a single stream event. Returns null if the event should not produce
    // a digest line (e.g. rate_limit_event, an assistant turn containing only
    // a thinking block, or an unknown event type). May throw if the JsonElement
    // is not an object - callers wrap in try/catch.
    internal string? FormatLine(JsonElement parsedLine, DateTimeOffset startTime)
    {
        if (parsedLine.ValueKind != JsonValueKind.Object) return null;
        if (!parsedLine.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var offset = FormatOffset(DateTimeOffset.UtcNow - startTime);
        var kind = typeEl.GetString();

        return kind switch
        {
            "system" => FormatSystem(parsedLine, offset),
            "assistant" => FormatAssistant(parsedLine, offset),
            "result" => FormatResult(parsedLine, offset),
            _ => null
        };
    }

    // Overload used by tests that pin the offset directly.
    internal string? FormatLine(JsonElement parsedLine, TimeSpan offset)
    {
        if (parsedLine.ValueKind != JsonValueKind.Object) return null;
        if (!parsedLine.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var offsetStr = FormatOffset(offset);
        var kind = typeEl.GetString();

        return kind switch
        {
            "system" => FormatSystem(parsedLine, offsetStr),
            "assistant" => FormatAssistant(parsedLine, offsetStr),
            "result" => FormatResult(parsedLine, offsetStr),
            _ => null
        };
    }

    private string? FormatSystem(JsonElement el, string offset)
    {
        return TryGetString(el, "subtype") switch
        {
            "init" => FormatInit(el, offset),
            "thinking_tokens" => FormatThinkingTokens(el, offset),
            // hook events, compact_boundary, future subtypes: not digest-worthy.
            _ => null
        };
    }

    private static string FormatInit(JsonElement el, string offset)
    {
        var sessionId = TryGetString(el, "session_id") ?? "";
        var shortId = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
        var model = TryGetString(el, "model") ?? "";
        var payload = $"init session {shortId} model {model}".TrimEnd();
        return $"[{offset}] {PadKind("system")} {Truncate(payload)}";
    }

    // Throttled ticker for system/thinking_tokens events. The event carries a
    // cumulative estimated_tokens count; emit a line each time it grows past the
    // next ThinkingTokensEmitStep boundary since the last emitted line.
    private string? FormatThinkingTokens(JsonElement el, string offset)
    {
        if (!el.TryGetProperty("estimated_tokens", out var est) || est.ValueKind != JsonValueKind.Number)
            return null;
        var tokens = est.TryGetInt64(out var l) ? l : (long)est.GetDouble();
        if (tokens < _lastThinkingTokens + ThinkingTokensEmitStep) return null;
        _lastThinkingTokens = tokens;
        var payload = string.Format(CultureInfo.InvariantCulture, "~{0:0.0}k tokens", tokens / 1000.0);
        return $"[{offset}] {PadKind("thinking")} {Truncate(payload)}";
    }

    private static string? FormatAssistant(JsonElement el, string offset)
    {
        if (!el.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return null;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        // If any block is tool_use, emit one digest line per tool_use block joined
        // by newlines. Otherwise emit a single "turn" marker (text/thinking only).
        var toolLines = new List<string>();
        bool hasNonToolBlock = false;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = TryGetString(block, "type");
            if (blockType == "tool_use")
            {
                var name = TryGetString(block, "name") ?? "?";
                var argSummary = SummarizeToolInput(block, name);
                var payload = string.IsNullOrEmpty(argSummary) ? name : $"{name}  {argSummary}";
                toolLines.Add($"[{offset}] {PadKind("tool_use")} {Truncate(payload)}");
            }
            else if (blockType == "text" || blockType == "thinking")
            {
                hasNonToolBlock = true;
            }
        }

        if (toolLines.Count > 0)
            return string.Join("\n", toolLines);

        if (hasNonToolBlock)
            return $"[{offset}] {PadKind("assistant")} turn";

        return null;
    }

    private static string FormatResult(JsonElement el, string offset)
    {
        var isError = el.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True;
        var status = isError ? "err" : "ok";

        int outTokens = 0;
        int cacheRead = 0;
        if (el.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                outTokens = ot.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.ValueKind == JsonValueKind.Number)
                cacheRead = cr.GetInt32();
        }
        var payload = $"{status}  {outTokens} out / {cacheRead} cache-read";
        return $"[{offset}] {PadKind("result")} {Truncate(payload)}";
    }

    // Surfaces the first interesting argument from a tool_use input block.
    // For Bash: strips leading "cd <worktree> && " so the actual command is visible.
    // For Read/Glob/path: shows just the last 2 path segments.
    // For Grep: shows the pattern.
    // Result is unbounded length - the caller truncates the full payload.
    private static string SummarizeToolInput(JsonElement block, string toolName)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return "";

        // Bash: strip the common "cd <worktree-path> && " prefix so the actual command is legible.
        if (toolName == "Bash")
        {
            var command = TryGetString(input, "command");
            if (!string.IsNullOrEmpty(command)) return StripWorktreePrefix(command!);
        }

        var fp = TryGetString(input, "file_path");
        if (!string.IsNullOrEmpty(fp)) return LastSegments(fp!, 2);

        var pattern = TryGetString(input, "pattern");
        if (!string.IsNullOrEmpty(pattern)) return pattern!;

        var command2 = TryGetString(input, "command");
        if (!string.IsNullOrEmpty(command2)) return command2!;

        var path = TryGetString(input, "path");
        if (!string.IsNullOrEmpty(path)) return LastSegments(path!, 2);

        var url = TryGetString(input, "url");
        if (!string.IsNullOrEmpty(url)) return url!;

        // Fallback: join the first one or two scalar fields.
        var sb = new StringBuilder();
        int n = 0;
        foreach (var prop in input.EnumerateObject())
        {
            if (n >= 2) break;
            if (prop.Value.ValueKind == JsonValueKind.String || prop.Value.ValueKind == JsonValueKind.Number)
            {
                if (n > 0) sb.Append(' ');
                sb.Append(prop.Name).Append('=').Append(prop.Value.ToString());
                n++;
            }
        }
        return sb.ToString();
    }

    // Strip leading "cd <path> && " from a bash command so the actual command is visible.
    // Handles one level of cd-and-run; leaves all other commands untouched.
    private static string StripWorktreePrefix(string command)
    {
        if (!command.StartsWith("cd ", StringComparison.Ordinal)) return command;
        var idx = command.IndexOf(" && ", StringComparison.Ordinal);
        if (idx < 0) return command;
        return command[(idx + 4)..].TrimStart();
    }

    // Return the last N slash- or backslash-separated segments of a path.
    private static string LastSegments(string path, int n)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= n) return path;
        return string.Join("/", parts[^n..]);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    // Format a TimeSpan as m:ss (or h:mm:ss if >= 1 hour). Zero-padded seconds.
    internal static string FormatOffset(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
        if (ts.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}",
                (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}",
            (int)ts.TotalMinutes, ts.Seconds);
    }

    private static string Truncate(string s)
    {
        if (s.Length <= MaxPayloadChars) return s;
        return s[..(MaxPayloadChars - 3)] + "...";
    }

    private static string PadKind(string kind)
    {
        // Left-aligned in a 10-char column for visual alignment across event kinds.
        return kind.PadRight(10);
    }
}
