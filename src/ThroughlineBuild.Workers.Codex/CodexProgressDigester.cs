using System.Globalization;
using System.Text.Json;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Workers.Codex;

// Converts a single NDJSON stream event from the Codex CLI (--json mode) into a
// one-line human-readable digest, or returns null for uninteresting events.
//
// Output format: [m:ss] kind       payload
// where kind is left-padded to 10 chars and payload is truncated to 80 chars.
//
// Codex --json event shapes handled:
//   "thread.started" - session/thread creation
//   "turn.started"   - model turn starts
//   "item.started"   - tool/command execution starts
//   "item.completed" - tool/command execution or agent message completes
//   "turn.completed" - model turn finishes with usage
// All other event types return null (graceful degradation).
public sealed class CodexProgressDigester : IWorkerProgressDigester
{
    internal const int MaxPayloadChars = 80;

    private DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    public void ResetStart() => _startTime = DateTimeOffset.UtcNow;

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

    internal string? FormatLine(JsonElement el, DateTimeOffset startTime)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var offset = FormatOffset(DateTimeOffset.UtcNow - startTime);
        var kind = typeEl.GetString();

        return FormatByType(el, kind, offset);
    }

    // Overload for tests that pin the offset directly.
    internal string? FormatLine(JsonElement el, TimeSpan offset)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var offsetStr = FormatOffset(offset);
        var kind = typeEl.GetString();

        return FormatByType(el, kind, offsetStr);
    }

    internal string FormatElapsed(DateTimeOffset now) => FormatOffset(now - _startTime);

    internal string? FormatActivity(string rawNdjsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawNdjsonLine);
            return FormatActivity(doc.RootElement);
        }
        catch { return null; }
    }

    private static string? FormatByType(JsonElement el, string? kind, string offset)
    {
        return kind switch
        {
            "thread.started" => FormatThreadStarted(el, offset),
            "turn.started" => $"[{offset}] {PadKind("turn")} started",
            "item.started" => FormatItem(el, offset, started: true),
            "item.completed" => FormatItem(el, offset, started: false),
            "turn.completed" => FormatTurnCompleted(el, offset),
            _ => null
        };
    }

    private static string? FormatActivity(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var kind = typeEl.GetString();
        return kind switch
        {
            "thread.started" => "session started",
            "turn.started" => "turn started",
            "item.started" or "item.completed" => FormatItemActivity(el),
            "turn.completed" => "turn completed",
            _ => null
        };
    }

    private static string? FormatThreadStarted(JsonElement el, string offset)
    {
        var threadId = TryGetString(el, "thread_id") ?? "";
        var shortId = threadId.Length > 8 ? threadId[..8] : threadId;
        var payload = string.IsNullOrEmpty(shortId) ? "started" : $"started {shortId}";
        return $"[{offset}] {PadKind("session")} {Truncate(payload)}";
    }

    private static string? FormatItem(JsonElement el, string offset, bool started)
    {
        if (!el.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return null;

        var itemType = TryGetString(item, "type");
        if (itemType == "command_execution")
            return FormatCommandExecution(item, offset, started);
        if (itemType == "agent_message")
            return FormatAgentMessage(item, offset);

        var status = started ? "started" : "completed";
        var payload = string.IsNullOrEmpty(itemType) ? status : $"{itemType} {status}";
        return $"[{offset}] {PadKind("item")} {Truncate(payload)}";
    }

    private static string? FormatCommandExecution(JsonElement item, string offset, bool started)
    {
        var command = TryGetString(item, "command") ?? "command";
        var payload = started
            ? SummarizeCommand(command)
            : $"{SummarizeCommand(command)} ({FormatCommandStatus(item)})";
        return $"[{offset}] {PadKind(started ? "tool_start" : "tool_done")} {Truncate(payload)}";
    }

    private static string? FormatAgentMessage(JsonElement item, string offset)
    {
        var text = TryGetString(item, "text") ?? "";
        if (text.Length == 0) return null;
        var excerpt = text.Length > 60 ? text[..60].Replace('\n', ' ') : text.Replace('\n', ' ');
        return $"[{offset}] {PadKind("message")} {Truncate(excerpt)}";
    }

    private static string FormatTurnCompleted(JsonElement el, string offset)
    {
        int inputTokens = 0;
        int outputTokens = 0;
        if (el.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                outputTokens = ot.GetInt32();
        }
        var payload = inputTokens == 0 && outputTokens == 0
            ? "completed"
            : $"{inputTokens} in / {outputTokens} out";
        return $"[{offset}] {PadKind("turn_done")} {Truncate(payload)}";
    }

    private static string? FormatItemActivity(JsonElement el)
    {
        if (!el.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return null;
        var itemType = TryGetString(item, "type");
        if (itemType == "command_execution")
        {
            var command = TryGetString(item, "command") ?? "command";
            var status = TryGetString(item, "status") ?? "running";
            return $"{status} {SummarizeCommand(command)}";
        }
        if (itemType == "agent_message")
            return "agent message";
        return itemType;
    }

    private static string FormatCommandStatus(JsonElement item)
    {
        var status = TryGetString(item, "status");
        if (item.TryGetProperty("exit_code", out var exitEl) && exitEl.ValueKind == JsonValueKind.Number)
            return $"exit {exitEl.GetInt32()}";
        return status ?? "completed";
    }

    private static string SummarizeCommand(string command)
    {
        var cleaned = command.Replace("\r", " ").Replace("\n", " ").Trim();
        const string pwshMarker = " -Command ";
        var markerIndex = cleaned.IndexOf(pwshMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            cleaned = cleaned[(markerIndex + pwshMarker.Length)..].Trim();
        return cleaned.Trim('"');
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    internal static string FormatOffset(TimeSpan ts)
    {
        if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
        if (ts.TotalHours >= 1)
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}",
                (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}",
            (int)ts.TotalMinutes, ts.Seconds);
    }

    private static string Truncate(string s)
        => s.Length <= MaxPayloadChars ? s : s[..(MaxPayloadChars - 3)] + "...";

    private static string PadKind(string kind) => kind.PadRight(10);
}
