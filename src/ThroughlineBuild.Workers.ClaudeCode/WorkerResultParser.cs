using System.Text.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal static class WorkerResultParser
{
    internal static WorkerResultParseOutcome TryParse(string stdout)
    {
        // Scan for "WORKER_RESULT" marker line, then accumulate everything after it
        // to end-of-input as a single JSON object. The template spec says the envelope
        // is the LAST output, and workers emit pretty-printed JSON spanning multiple
        // lines, so the parser cannot rely on a single-line payload.
        //
        // Uses the source-gen overload (ClaudeCodeJsonContext) so deserialization works
        // under PublishAot=true where reflection-based JsonSerializer.Deserialize<T>
        // throws NotSupportedException. See docs/throughline-build-architecture.md,
        // section "AOT serialization traps".
        var lines = stdout.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "WORKER_RESULT") continue;

            var json = string.Join("\n", lines, i + 1, lines.Length - i - 1).Trim();
            try
            {
                var dto = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);
                if (dto is null)
                    return WorkerResultParseOutcome.DeserializeFailed("JsonElement", "Deserialization returned null");
                if (dto.Status is null)
                    return WorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing required 'status' field. Payload: {json}");
                if (string.IsNullOrWhiteSpace(dto.Summary))
                    return WorkerResultParseOutcome.DeserializeFailed("ValidationError",
                        $"WORKER_RESULT JSON missing or empty 'summary' field. Payload: {json}");

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
                return WorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return WorkerResultParseOutcome.DeserializeFailed(ex.GetType().Name, ex.Message);
            }
        }
        return WorkerResultParseOutcome.MarkerMissing();
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
