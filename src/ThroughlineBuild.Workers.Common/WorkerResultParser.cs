using System.Diagnostics.CodeAnalysis;
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
    [JsonPropertyName("tickets")]
    public List<BatchTicketResultDto>? Tickets { get; set; }
}

internal sealed class BatchWorkerResultDto
{
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
    [JsonPropertyName("tickets")]
    public List<BatchTicketResultDto>? Tickets { get; set; }
}

internal sealed class BatchTicketResultDto
{
    [JsonPropertyName("ticket_id")]
    public string? TicketId { get; set; }
    [JsonPropertyName("commit_sha")]
    public string? CommitSha { get; set; }
    [JsonPropertyName("stack_position")]
    public int? StackPosition { get; set; }
    [JsonPropertyName("files_changed")]
    public List<string>? FilesChanged { get; set; }
    [JsonPropertyName("summary_ref")]
    public string? SummaryRef { get; set; }
}

[JsonSerializable(typeof(WorkerResultDto))]
[JsonSerializable(typeof(BatchWorkerResultDto))]
[JsonSerializable(typeof(BatchTicketResultDto))]
[JsonSerializable(typeof(BatchTicketResult))]
[JsonSerializable(typeof(BatchWorkerResult))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(CompletionClaimDto))]
[JsonSerializable(typeof(AcBindingDto))]
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

        // Pre-pass: extract named fenced blocks before WORKER_RESULT
        if (!TryScanFencedBlocks(lines, out var blocks, out var scanErrorType, out var scanErrorMessage))
            return WorkerResultParseOutcome.FenceScanFailed(scanErrorType!, scanErrorMessage!);

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
                    // The payload deserialized as a valid JSON object but carried no 'status' key
                    // (or status:null). Flag this specific subcase via MissingStatus so the vendor
                    // agents can tag a committed-but-non-conforming session as recoverable. A JSON
                    // syntax error or an unrecognized status enum value instead lands in the
                    // catch(JsonException) below and is NOT flagged. See TLB-476.
                    lastFailure = WorkerResultParseOutcome.MissingStatus(
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

                // Parse optional top-level tickets array (batch implement responses).
                IReadOnlyList<BatchTicketResult>? tickets = null;
                if (dto.Tickets is { Count: > 0 })
                {
                    var ticketList = new List<BatchTicketResult>(dto.Tickets.Count);
                    bool ticketValidationFailed = false;
                    for (int ti = 0; ti < dto.Tickets.Count; ti++)
                    {
                        if (!TryMapBatchTicket(dto.Tickets[ti], ti, out var mapped, out var msg))
                        {
                            lastFailure = WorkerResultParseOutcome.DeserializeFailed("ValidationError", msg!);
                            ticketValidationFailed = true;
                            break;
                        }
                        ticketList.Add(mapped);
                    }
                    if (ticketValidationFailed) continue;
                    tickets = ticketList.AsReadOnly();
                }

                var result = new WorkerResult(
                    dto.Status.Value,
                    dto.Summary,
                    files,
                    dto.FailureReason,
                    meta,
                    Tickets: tickets);
                return WorkerResultParseOutcome.Success(result, blocks);
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

    internal static BatchWorkerResultParseOutcome TryParseBatch(string stdout)
    {
        var lines = stdout.Split('\n');

        if (!TryScanFencedBlocks(lines, out var blocks, out var scanErrorType, out var scanErrorMessage))
            return BatchWorkerResultParseOutcome.FenceScanFailed(scanErrorType!, scanErrorMessage!);

        var markerIndices = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "WORKER_RESULT")
                markerIndices.Add(i);
        }
        if (markerIndices.Count == 0)
            return BatchWorkerResultParseOutcome.MarkerMissing();

        BatchWorkerResultParseOutcome lastFailure = BatchWorkerResultParseOutcome.MarkerMissing();
        for (int idx = markerIndices.Count - 1; idx >= 0; idx--)
        {
            int i = markerIndices[idx];
            var json = StripCodeFence(string.Join("\n", lines, i + 1, lines.Length - i - 1).Trim());
            try
            {
                var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.BatchWorkerResultDto);
                if (dto is null)
                {
                    lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("JsonElement", "Deserialization returned null");
                    continue;
                }
                if (dto.Status is null)
                {
                    lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing required 'status' field. Payload: {json}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(dto.Summary))
                {
                    lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing or empty 'summary' field. Payload: {json}");
                    continue;
                }
                if (dto.Tickets is null)
                {
                    lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        "batch WORKER_RESULT JSON missing required non-empty 'tickets' array");
                    continue;
                }
                if (dto.Tickets.Count == 0)
                {
                    lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        "batch WORKER_RESULT JSON has empty 'tickets' array");
                    continue;
                }

                var tickets = new List<BatchTicketResult>(dto.Tickets.Count);
                var validationFailed = false;
                for (int ticketIndex = 0; ticketIndex < dto.Tickets.Count; ticketIndex++)
                {
                    var ticket = dto.Tickets[ticketIndex];
                    if (!TryMapBatchTicket(ticket, ticketIndex, out var mappedTicket, out var validationMessage))
                    {
                        lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed("ValidationError", validationMessage!);
                        validationFailed = true;
                        break;
                    }

                    tickets.Add(mappedTicket);
                }

                if (validationFailed)
                    continue;

                IReadOnlyList<string> files = dto.FilesChanged ?? new List<string>();
                IReadOnlyDictionary<string, JsonElement> meta = dto.Metadata is not null
                    ? dto.Metadata
                    : new Dictionary<string, JsonElement>();
                var result = new BatchWorkerResult(
                    dto.Status.Value,
                    dto.Summary,
                    files,
                    dto.FailureReason,
                    meta,
                    tickets);
                return BatchWorkerResultParseOutcome.Success(result, blocks);
            }
            catch (JsonException ex)
            {
                lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
            }
            catch (NotSupportedException ex)
            {
                lastFailure = BatchWorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
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

    private static bool TryScanFencedBlocks(
        string[] lines,
        out Dictionary<string, string> blocks,
        out string? errorType,
        out string? errorMessage)
    {
        blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        errorType = null;
        errorMessage = null;

        string? currentName = null;
        var currentContent = new System.Text.StringBuilder();
        int currentStartLine = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            // Stop scanning when we hit the WORKER_RESULT marker
            if (trimmed == "WORKER_RESULT")
                break;

            if (trimmed.StartsWith("<<<", StringComparison.Ordinal))
            {
                if (trimmed.EndsWith("_START", StringComparison.Ordinal))
                {
                    // Extract name: everything between <<< and _START
                    var name = trimmed.Substring(3, trimmed.Length - 3 - 6);
                    if (currentName != null)
                    {
                        errorType = "FenceScanError";
                        errorMessage = $"unclosed fenced block '{currentName}' (opened at line {currentStartLine}); found '<<<{name}_START' at line {i}";
                        return false;
                    }
                    if (!IsValidBlockName(name))
                    {
                        errorType = "FenceScanError";
                        errorMessage = $"invalid block name '{name}' at line {i}; must match ^[A-Z][A-Z0-9_]*$";
                        return false;
                    }
                    if (blocks.ContainsKey(name))
                    {
                        errorType = "FenceScanError";
                        errorMessage = $"duplicate block name '{name}' at line {i}";
                        return false;
                    }
                    currentName = name;
                    currentStartLine = i;
                    currentContent.Clear();
                }
                else if (trimmed.EndsWith("_END", StringComparison.Ordinal))
                {
                    var name = trimmed.Substring(3, trimmed.Length - 3 - 4);
                    if (currentName == null)
                    {
                        errorType = "FenceScanError";
                        errorMessage = $"unexpected close marker '<<<{name}_END' at line {i} with no open block";
                        return false;
                    }
                    if (!string.Equals(name, currentName, StringComparison.Ordinal))
                    {
                        errorType = "FenceScanError";
                        errorMessage = $"mismatched fence: expected '<<<{currentName}_END', found '<<<{name}_END' at line {i}";
                        return false;
                    }
                    blocks[currentName] = currentContent.ToString();
                    currentName = null;
                    currentContent.Clear();
                    currentStartLine = -1;
                }
            }
            else if (currentName != null)
            {
                // Accumulate content lines
                if (currentContent.Length > 0)
                    currentContent.Append('\n');
                currentContent.Append(lines[i]);
            }
        }

        if (currentName != null)
        {
            errorType = "FenceScanError";
            errorMessage = $"unclosed fenced block '{currentName}' (opened at line {currentStartLine}); reached end of output without '<<<{currentName}_END'";
            return false;
        }

        return true;
    }

    private static bool IsValidBlockName(string name)
    {
        if (name.Length == 0) return false;
        if (!char.IsUpper(name[0])) return false;
        foreach (char c in name)
        {
            if (!char.IsUpper(c) && !char.IsDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    private static bool TryMapBatchTicket(
        BatchTicketResultDto ticket,
        int ticketIndex,
        [NotNullWhen(true)] out BatchTicketResult? result,
        [NotNullWhen(false)] out string? validationMessage)
    {
        result = null;
        validationMessage = null;
        var path = $"tickets[{ticketIndex}]";

        if (string.IsNullOrWhiteSpace(ticket.TicketId))
        {
            validationMessage = $"{path}.ticket_id is required and must be a non-empty string";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ticket.CommitSha))
        {
            validationMessage = $"{path}.commit_sha is required and must be a non-empty string";
            return false;
        }
        if (ticket.StackPosition is null)
        {
            validationMessage = $"{path}.stack_position is required";
            return false;
        }
        if (ticket.FilesChanged is null)
        {
            validationMessage = $"{path}.files_changed is required and must be an array";
            return false;
        }
        if (ticket.FilesChanged.Any(string.IsNullOrWhiteSpace))
        {
            validationMessage = $"{path}.files_changed entries must be non-empty strings";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ticket.SummaryRef))
        {
            validationMessage = $"{path}.summary_ref is required and must be a non-empty string";
            return false;
        }

        result = new BatchTicketResult(
            ticket.TicketId,
            ticket.CommitSha,
            ticket.StackPosition.Value,
            ticket.FilesChanged,
            ticket.SummaryRef);
        return true;
    }
}

internal readonly record struct WorkerResultParseOutcome(
    WorkerResult? Result,
    string? DeserializeErrorType,
    string? DeserializeErrorMessage,
    IReadOnlyDictionary<string, string> Blocks)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyBlocks =
        new Dictionary<string, string>();

    // True only for the "payload parsed as a valid JSON object but the required 'status'
    // field was absent or null" failure. Vendor agents read this to tag the WorkerResult
    // with envelope_status=missing_status so ImplementPhase can salvage a committed, clean
    // session whose only fault was a non-conforming trailing envelope. Stays false for JSON
    // syntax errors, unrecognized status enum values, missing summary, escalation/fence
    // errors, and the success path. See TLB-476.
    internal bool MissingStatusField { get; init; }

    internal static WorkerResultParseOutcome Success(WorkerResult result, IReadOnlyDictionary<string, string> blocks) =>
        new(Result: result, DeserializeErrorType: null, DeserializeErrorMessage: null, Blocks: blocks);

    internal static WorkerResultParseOutcome MarkerMissing() =>
        new(Result: null, DeserializeErrorType: null, DeserializeErrorMessage: null, Blocks: EmptyBlocks);

    internal static WorkerResultParseOutcome DeserializeFailed(string errorType, string errorMessage) =>
        new(Result: null, DeserializeErrorType: errorType, DeserializeErrorMessage: errorMessage, Blocks: EmptyBlocks);

    // Specialized DeserializeFailed for the recoverable "valid JSON object, no status key" case.
    internal static WorkerResultParseOutcome MissingStatus(string errorMessage) =>
        new(Result: null, DeserializeErrorType: "ValidationError", DeserializeErrorMessage: errorMessage, Blocks: EmptyBlocks)
        { MissingStatusField = true };

    internal static WorkerResultParseOutcome FenceScanFailed(string errorType, string errorMessage) =>
        new(Result: null, DeserializeErrorType: errorType, DeserializeErrorMessage: errorMessage, Blocks: EmptyBlocks);
}

internal readonly record struct BatchWorkerResultParseOutcome(
    BatchWorkerResult? Result,
    string? DeserializeErrorType,
    string? DeserializeErrorMessage,
    IReadOnlyDictionary<string, string> Blocks)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyBlocks =
        new Dictionary<string, string>();

    internal static BatchWorkerResultParseOutcome Success(BatchWorkerResult result, IReadOnlyDictionary<string, string> blocks) =>
        new(Result: result, DeserializeErrorType: null, DeserializeErrorMessage: null, Blocks: blocks);

    internal static BatchWorkerResultParseOutcome MarkerMissing() =>
        new(Result: null, DeserializeErrorType: null, DeserializeErrorMessage: null, Blocks: EmptyBlocks);

    internal static BatchWorkerResultParseOutcome DeserializeFailed(string errorType, string errorMessage) =>
        new(Result: null, DeserializeErrorType: errorType, DeserializeErrorMessage: errorMessage, Blocks: EmptyBlocks);

    internal static BatchWorkerResultParseOutcome FenceScanFailed(string errorType, string errorMessage) =>
        new(Result: null, DeserializeErrorType: errorType, DeserializeErrorMessage: errorMessage, Blocks: EmptyBlocks);
}

internal static class FencedBlockResolver
{
    // Resolves a _ref field from the envelope metadata to block content.
    // refFieldName: the metadata key (e.g. "plan_body_ref")
    // Returns true and sets content if the ref resolves; returns false and sets error on failure.
    internal static bool TryResolveRef(
        IReadOnlyDictionary<string, string> blocks,
        IReadOnlyDictionary<string, object> metadata,
        string refFieldName,
        [NotNullWhen(true)] out string? content,
        out string? error)
    {
        content = null;
        error = null;

        if (!metadata.TryGetValue(refFieldName, out var refObj))
        {
            error = $"metadata field '{refFieldName}' not found";
            return false;
        }

        string? blockName = null;
        if (refObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
            blockName = je.GetString();
        else if (refObj is string s)
            blockName = s;

        if (string.IsNullOrWhiteSpace(blockName))
        {
            error = $"metadata field '{refFieldName}' is not a non-empty string";
            return false;
        }

        if (!blocks.TryGetValue(blockName, out content))
        {
            error = $"referenced block not found: '{blockName}' (referenced by metadata field '{refFieldName}')";
            return false;
        }

        return true;
    }
}
