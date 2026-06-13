using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal sealed record ClaudePersistedTranscript(
    string AssistantTranscript,
    string RedactedAssistantTranscript,
    string? Model,
    string? ClaudeCodeVersion,
    ClaudeCodeUsage? Usage,
    IReadOnlyList<(DateTimeOffset At, string Line)> NormalizedLines,
    IReadOnlyList<(DateTimeOffset At, string Line)> RedactedNormalizedLines,
    string RedactedRawTranscript,
    string? ProviderErrorText,
    int SkippedLines);

// Isolates the installed Claude Code persisted-session schema from transport and phase code.
// Only assistant text is required. Every other field is optional and unknown event kinds are
// retained as harmless decorations or ignored.
internal static class ClaudePersistedTranscriptReader
{
    private static readonly Regex InlineSecret = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|password|client[_-]?secret|authorization)\b(\s*[:=]\s*|\s+bearer\s+)\S+",
        RegexOptions.Compiled);

    public static ClaudePersistedTranscript Read(string path, string expectedSessionId)
    {
        var rawRedacted = new StringBuilder();
        var normalized = new List<(DateTimeOffset, string)>();
        var redactedNormalized = new List<(DateTimeOffset, string)>();
        var turns = new Dictionary<string, TurnUsage>(StringComparer.Ordinal);
        string? model = null;
        string? version = null;
        string? providerError = null;
        string? observedSession = null;
        int skipped = 0;
        var fallbackAt = DateTimeOffset.UtcNow;

        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(rawLine); }
            catch (JsonException) { skipped++; continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                observedSession ??= GetString(root, "sessionId") ?? GetString(root, "session_id");
                version ??= GetString(root, "version") ?? GetString(root, "claude_code_version");
                var at = GetTimestamp(root) ?? fallbackAt;
                fallbackAt = at.AddTicks(1);

                var redacted = Redact(root);
                rawRedacted.AppendLine(redacted);
                normalized.Add((at, rawLine));
                redactedNormalized.Add((at, redacted));

                var type = GetString(root, "type");
                if (type == "assistant" && root.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object)
                {
                    model ??= GetString(message, "model");
                    var messageId = GetString(message, "id") ?? GetString(root, "uuid") ?? $"line-{normalized.Count}";
                    if (message.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                        turns[messageId] = ReadUsage(usageElement);
                }

                providerError ??= TryReadProviderError(root, type);
            }
        }

        if (!string.IsNullOrWhiteSpace(observedSession)
            && !string.Equals(observedSession, expectedSessionId, StringComparison.Ordinal))
            throw new InvalidDataException("Persisted transcript session id did not match the Stop-hook completion.");

        var usage = turns.Count == 0 ? null : new ClaudeCodeUsage(
            SumNullable(turns.Values.Select(x => x.Input)),
            SumNullable(turns.Values.Select(x => x.Output)),
            SumNullable(turns.Values.Select(x => x.CacheRead)),
            SumNullable(turns.Values.Select(x => x.CacheCreate)));

        var start = normalized.Count > 0 ? normalized[0].Item1 : DateTimeOffset.UtcNow;
        var system = BuildSystemLine(expectedSessionId, model, version);
        normalized.Insert(0, (start, system));
        redactedNormalized.Insert(0, (start, system));
        var resultLine = BuildResultLine(usage, providerError is not null);
        normalized.Add((fallbackAt, resultLine));
        redactedNormalized.Add((fallbackAt, resultLine));

        // Reconstruct both transcripts through the single shared extractor so the parsed
        // assistant text and the redacted debug artifact stay byte-for-byte equivalent
        // (modulo redaction) instead of drifting across two hand-written code paths.
        var assistant = ClaudeCodeAgent.TryExtractAssistantTranscript(
            string.Join('\n', normalized.Select(line => line.Item2))) ?? "";
        var redactedAssistant = ClaudeCodeAgent.TryExtractAssistantTranscript(
            string.Join('\n', redactedNormalized.Select(line => line.Item2))) ?? "";
        return new ClaudePersistedTranscript(assistant, redactedAssistant, model, version, usage,
            normalized, redactedNormalized, rawRedacted.ToString(), providerError, skipped);
    }

    private static TurnUsage ReadUsage(JsonElement usage) => new(
        GetInt(usage, "input_tokens"), GetInt(usage, "output_tokens"),
        GetInt(usage, "cache_read_input_tokens"), GetInt(usage, "cache_creation_input_tokens"));

    private static int? SumNullable(IEnumerable<int?> values)
    {
        // Accumulate into a long so a pathological token total cannot throw
        // OverflowException inside Read() (which would be caught as a telemetry
        // failure and drop the recovered assistant text too). Saturate at int.MaxValue
        // rather than throw; real token sums never approach that ceiling.
        long sum = 0;
        var any = false;
        foreach (var value in values)
        {
            if (value is not int present) continue;
            any = true;
            sum += present;
        }
        return any ? (int)Math.Min(sum, int.MaxValue) : null;
    }

    private static string BuildSystemLine(string sessionId, string? model, string? version) =>
        WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "system");
            writer.WriteString("subtype", "init");
            writer.WriteString("session_id", sessionId);
            if (model is not null) writer.WriteString("model", model);
            if (version is not null) writer.WriteString("claude_code_version", version);
            writer.WriteEndObject();
        });

    private static string BuildResultLine(ClaudeCodeUsage? usage, bool isError) =>
        WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "result");
            writer.WriteString("subtype", isError ? "error" : "success");
            writer.WriteBoolean("is_error", isError);
            if (usage is not null)
            {
                writer.WriteStartObject("usage");
                if (usage.InputTokens is int input) writer.WriteNumber("input_tokens", input);
                if (usage.OutputTokens is int output) writer.WriteNumber("output_tokens", output);
                if (usage.CacheReadInputTokens is int read) writer.WriteNumber("cache_read_input_tokens", read);
                if (usage.CacheCreationInputTokens is int create) writer.WriteNumber("cache_creation_input_tokens", create);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryReadProviderError(JsonElement root, string? type)
    {
        if (type is not ("system" or "assistant" or "progress" or "error")) return null;
        var candidate = GetString(root, "error") ?? GetString(root, "message");
        if (candidate is null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            candidate = GetString(data, "error") ?? GetString(data, "message");
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var lower = candidate.ToLowerInvariant();
        return lower.Contains("usage limit") || lower.Contains("rate limit") || lower.Contains("quota")
            || lower.Contains("unauthorized") || lower.Contains("authentication") || lower.Contains("login")
            ? candidate.Trim() : null;
    }

    private static DateTimeOffset? GetTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(GetString(root, "timestamp"), out var parsed) ? parsed : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed) ? parsed : null;

    internal static string Redact(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(writer, root, null);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        if (propertyName is not null && IsSensitiveName(propertyName))
        {
            writer.WriteStringValue("[REDACTED]");
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteRedacted(writer, item, propertyName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(value.GetString() ?? ""));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    internal static string RedactText(string value) => InlineSecret.Replace(value, "$1=[REDACTED]");

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace('-', '_').ToLowerInvariant();
        if (normalized.EndsWith("_tokens", StringComparison.Ordinal)) return false;
        return normalized is "api_key" or "apikey" or "authorization" or "password" or "secret"
            or "token" or "access_token" or "refresh_token" or "cookie" or "client_secret"
            || normalized.EndsWith("_password", StringComparison.Ordinal)
            || normalized.EndsWith("_secret", StringComparison.Ordinal)
            || normalized.EndsWith("_token", StringComparison.Ordinal);
    }

    private sealed record TurnUsage(int? Input, int? Output, int? CacheRead, int? CacheCreate);
}
