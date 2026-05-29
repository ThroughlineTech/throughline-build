using System.Text.Json;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Common;

// DTO for deserializing the WORKER_RESULT JSON block emitted by agent workers.
// File-scoped so it can be registered with WorkersCommonJsonContext for source-gen.
// AOT trap: Dictionary<string, object> is not AOT-serializable; use
// Dictionary<string, JsonElement> instead. Callers that read metadata values
// should handle JsonElement (the phases already do via their TryGetString helpers).
internal sealed class WorkerResultDto
{
    // [JsonConverter] is required here: source-gen does not inherit the
    // CamelCase enum policy from JsonSerializerOptions; it must be wired
    // per-property. JsonStringEnumConverter<T> performs case-insensitive
    // matching on read, so PascalCase worker output ("Ok", "NeedsRework",
    // "Failed", "Escalate") and camelCase output are both accepted.
    // Status is nullable so the parser can detect a missing 'status' key
    // and fail loudly rather than silently defaulting to Ok (the enum zero).
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    public Status? Status { get; set; }
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
    [JsonPropertyName("files_changed")]
    public List<string>? FilesChanged { get; set; }
    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; set; }
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

[JsonSerializable(typeof(WorkerResultDto))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class WorkersCommonJsonContext : JsonSerializerContext { }

/// <summary>
/// Scans agent stdout for a WORKER_RESULT envelope and deserializes it into a
/// <see cref="WorkerResult"/>.
///
/// WORKER_RESULT envelope schema:
///   - Marker line: the literal string "WORKER_RESULT" on its own line (leading/
///     trailing whitespace is tolerated).
///   - Payload: a JSON object immediately following the marker, optionally wrapped
///     in a triple-backtick code fence (with or without a language tag such as "json").
///   - Required fields:
///       status   (string) - one of: Ok, NeedsRework, Failed, Escalate
///       summary  (string, non-empty) - one-line human-readable description
///   - Optional fields:
///       files_changed  (string array) - list of paths modified by the worker
///       failure_reason (string or null) - root cause when status != Ok
///       metadata       (object) - arbitrary key/value pairs (values as JsonElement)
///   - metadata.escalation (object, optional):
///       Present when the worker sets status Escalate and wants to convey structured
///       escalation context. Fields:
///         reason      (string) - why the worker escalated. Recognized values:
///                       "obsolete" - this ticket is superseded by another commit/ticket.
///                     Unknown reasons are accepted without parser failure; the
///                     orchestrator treats them as "unknown escalation reason - no auto-resolve".
///         subsumed_by (object, required when reason == "obsolete"):
///           commit    (string, non-empty) - SHA of the commit that subsumes this ticket
///           files     (string array, non-empty) - paths already handled by that commit
///           rationale (string, non-empty) - explanation of why the ticket is now obsolete
///       Parser rule: if reason == "obsolete" and subsumed_by is missing or incomplete
///       (any of commit/files/rationale absent, empty, or the wrong type), the parse fails
///       with ValidationError. Unknown reasons pass through without a subsumed_by check.
///   - Multiple markers are tolerated; the LAST valid envelope wins (the first
///     marker is often a template echo with placeholder text).
/// </summary>
internal static class WorkerResultParser
{
    internal static WorkerResultParseOutcome TryParse(string stdout)
    {
        // Scan for "WORKER_RESULT" marker line, then accumulate everything after it
        // to end-of-input as a single JSON object. The template spec says the envelope
        // is the LAST output, and workers emit pretty-printed JSON spanning multiple
        // lines, so the parser cannot rely on a single-line payload.
        //
        // Walk markers in reverse so the LAST envelope wins - tolerates the worker
        // echoing the template example block before emitting its real envelope.
        //
        // Uses the source-gen overload (WorkersCommonJsonContext) so deserialization
        // works under PublishAot=true where reflection-based JsonSerializer.Deserialize<T>
        // throws NotSupportedException. See docs/throughline-build-architecture.md,
        // section "AOT serialization traps".
        var lines = stdout.Split('\n');
        var markerIndices = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "WORKER_RESULT")
                markerIndices.Add(i);
        }
        if (markerIndices.Count == 0)
            return WorkerResultParseOutcome.MarkerMissing();

        WorkerResultParseOutcome lastFailure = WorkerResultParseOutcome.MarkerMissing();
        for (int idx = markerIndices.Count - 1; idx >= 0; idx--)
        {
            int i = markerIndices[idx];
            var json = StripCodeFence(string.Join("\n", lines, i + 1, lines.Length - i - 1).Trim());
            try
            {
                var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.WorkerResultDto);
                if (dto is null)
                {
                    lastFailure = WorkerResultParseOutcome.DeserializeFailed("JsonElement", "Deserialization returned null");
                    continue;
                }
                if (dto.Status is null)
                {
                    lastFailure = WorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing required 'status' field. Payload: {json}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(dto.Summary))
                {
                    lastFailure = WorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing or empty 'summary' field. Payload: {json}");
                    continue;
                }

                // Validate metadata.escalation when present.
                // Only "obsolete" reason requires the subsumed_by block; unknown reasons pass through.
                if (dto.Metadata is not null
                    && dto.Metadata.TryGetValue("escalation", out var escalationEl)
                    && escalationEl.ValueKind == JsonValueKind.Object
                    && escalationEl.TryGetProperty("reason", out var reasonEl)
                    && reasonEl.ValueKind == JsonValueKind.String
                    && string.Equals(reasonEl.GetString(), "obsolete", StringComparison.OrdinalIgnoreCase))
                {
                    bool subsumedByOk =
                        escalationEl.TryGetProperty("subsumed_by", out var subsumedByEl)
                        && subsumedByEl.ValueKind == JsonValueKind.Object
                        && subsumedByEl.TryGetProperty("commit", out var commitEl)
                        && commitEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(commitEl.GetString())
                        && subsumedByEl.TryGetProperty("files", out var filesEl)
                        && filesEl.ValueKind == JsonValueKind.Array
                        && filesEl.GetArrayLength() > 0
                        && subsumedByEl.TryGetProperty("rationale", out var rationaleEl)
                        && rationaleEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(rationaleEl.GetString());
                    if (!subsumedByOk)
                    {
                        lastFailure = WorkerResultParseOutcome.DeserializeFailed("ValidationError",
                            "metadata.escalation.reason is 'obsolete' but subsumed_by is missing or incomplete (requires commit string, non-empty files array, and rationale string)");
                        continue;
                    }
                }

                IReadOnlyList<string> files = dto.FilesChanged ?? new List<string>();
                IReadOnlyDictionary<string, object> meta = dto.Metadata is not null
                    ? dto.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
                    : new Dictionary<string, object>();
                var result = new WorkerResult(
                    dto.Status.Value,
                    dto.Summary,
                    files,
                    dto.FailureReason,
                    meta);
                return WorkerResultParseOutcome.Success(result);
            }
            catch (JsonException ex)
            {
                lastFailure = WorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
            }
            catch (NotSupportedException ex)
            {
                lastFailure = WorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
            }
        }
        return lastFailure;
    }

    // Strips a leading markdown opening fence (```, optionally followed by a
    // language tag like "json") and a trailing closing fence (```) when both
    // appear as standalone first / last lines of the payload. Other backticks
    // are left untouched so legitimate content inside JSON string values
    // survives unchanged. Workers - particularly Sonnet - mirror the template's
    // fenced example layout when emitting their real envelope; without this
    // strip the deserializer sees a backtick at byte 0 and aborts.
    //
    // Also handles the "whole block in a fence" case: if the model wraps the
    // WORKER_RESULT marker itself in a code fence (e.g. ```\nWORKER_RESULT\n{...}\n```),
    // the marker scanner lands on the line inside the fence and the substring
    // passed here starts with "{" - no opening fence, but a trailing "```" is
    // left over. StripTrailingFence removes it.
    private static string StripCodeFence(string payload)
    {
        if (payload.Length == 0) return payload;

        int newlineIdx = payload.IndexOf('\n');
        if (newlineIdx < 0) return payload;
        var firstLine = payload.Substring(0, newlineIdx).TrimEnd('\r');
        if (!IsOpeningFence(firstLine))
            return StripTrailingFence(payload);

        var rest = payload.Substring(newlineIdx + 1);

        // Find the first standalone closing fence line. Content after it (e.g., model
        // narration emitted after the fence) is discarded rather than passed to the
        // JSON parser, which would choke on the backtick.
        var restLines = rest.Split('\n');
        for (int i = 0; i < restLines.Length; i++)
        {
            if (restLines[i].TrimEnd() == "```")
                return string.Join("\n", restLines, 0, i).TrimEnd('\n', '\r');
        }

        return rest.TrimEnd();
    }

    // Scans from the end of the payload, skipping blank lines, and strips a lone
    // closing fence ("```") if it is the last non-blank content. This handles the
    // case where the model wraps the WORKER_RESULT block (including the marker) in
    // a code fence that opened before the marker line.
    private static string StripTrailingFence(string payload)
    {
        var lines = payload.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r', ' ', '\t');
            if (line == "```")
                return string.Join("\n", lines, 0, i).TrimEnd('\n', '\r');
            if (line.Length > 0)
                break;
        }
        return payload;
    }

    private static bool IsOpeningFence(string line)
    {
        if (!line.StartsWith("```", StringComparison.Ordinal)) return false;
        // Allow an optional language tag (letters/digits/+/-) after the fence.
        for (int i = 3; i < line.Length; i++)
        {
            char c = line[i];
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '_'))
                return false;
        }
        return true;
    }
}

internal readonly record struct WorkerResultParseOutcome(WorkerResult? Result, string? DeserializeErrorType, string? DeserializeErrorMessage)
{
    internal static WorkerResultParseOutcome Success(WorkerResult result) =>
        new(Result: result, DeserializeErrorType: null, DeserializeErrorMessage: null);

    internal static WorkerResultParseOutcome MarkerMissing() =>
        new(Result: null, DeserializeErrorType: null, DeserializeErrorMessage: null);

    internal static WorkerResultParseOutcome DeserializeFailed(string errorType, string errorMessage) =>
        new(Result: null, DeserializeErrorType: errorType, DeserializeErrorMessage: errorMessage);
}
