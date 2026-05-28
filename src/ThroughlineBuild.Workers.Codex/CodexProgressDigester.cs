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
//   "message"    - text content; surface a short excerpt
//   "tool_call"  - tool invocation; surface tool name + first arg
//   "tool_result"- tool result; surface tool name
//   "done"       - terminal event; surface exit status
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

        return kind switch
        {
            "message"     => FormatMessage(el, offset),
            "tool_call"   => FormatToolCall(el, offset),
            "tool_result" => FormatToolResult(el, offset),
            "done"        => FormatDone(el, offset),
            _             => null
        };
    }

    // Overload for tests that pin the offset directly.
    internal string? FormatLine(JsonElement el, TimeSpan offset)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;

        var offsetStr = FormatOffset(offset);
        var kind = typeEl.GetString();

        return kind switch
        {
            "message"     => FormatMessage(el, offsetStr),
            "tool_call"   => FormatToolCall(el, offsetStr),
            "tool_result" => FormatToolResult(el, offsetStr),
            "done"        => FormatDone(el, offsetStr),
            _             => null
        };
    }

    private static string? FormatMessage(JsonElement el, string offset)
    {
        var text = TryGetString(el, "content") ?? TryGetString(el, "text") ?? "";
        if (text.Length == 0) return null;
        var excerpt = text.Length > 60 ? text[..60].Replace('\n', ' ') : text.Replace('\n', ' ');
        return $"[{offset}] {PadKind("message")} {Truncate(excerpt)}";
    }

    private static string? FormatToolCall(JsonElement el, string offset)
    {
        var name = TryGetString(el, "name") ?? TryGetString(el, "function") ?? "?";
        var argSummary = TryGetFirstArgSummary(el);
        var payload = string.IsNullOrEmpty(argSummary) ? name : $"{name}  {argSummary}";
        return $"[{offset}] {PadKind("tool_call")} {Truncate(payload)}";
    }

    private static string? FormatToolResult(JsonElement el, string offset)
    {
        var name = TryGetString(el, "name") ?? TryGetString(el, "tool_name") ?? "?";
        return $"[{offset}] {PadKind("tool_result")} {Truncate(name)}";
    }

    private static string FormatDone(JsonElement el, string offset)
    {
        var exitCode = el.TryGetProperty("exit_code", out var ec) && ec.ValueKind == JsonValueKind.Number
            ? ec.GetInt32() : 0;
        var status = exitCode == 0 ? "ok" : $"exit {exitCode}";
        return $"[{offset}] {PadKind("done")} {Truncate(status)}";
    }

    private static string? TryGetFirstArgSummary(JsonElement el)
    {
        if (el.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    return prop.Value.ToString();
                break;
            }
        }
        return null;
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
