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
        // Walk markers in reverse so the LAST envelope wins - tolerates the worker
        // echoing the template example block before emitting its real envelope.
        //
        // Uses the source-gen overload (ClaudeCodeJsonContext) so deserialization works
        // under PublishAot=true where reflection-based JsonSerializer.Deserialize<T>
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
                var dto = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);
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
    private static string StripCodeFence(string payload)
    {
        if (payload.Length == 0) return payload;

        int newlineIdx = payload.IndexOf('\n');
        if (newlineIdx < 0) return payload;
        var firstLine = payload.Substring(0, newlineIdx).TrimEnd('\r');
        if (!IsOpeningFence(firstLine)) return payload;

        var rest = payload.Substring(newlineIdx + 1);
        var trimmedRest = rest.TrimEnd();
        if (trimmedRest.EndsWith("```", StringComparison.Ordinal))
        {
            int closeStart = trimmedRest.Length - 3;
            // Closing fence must be on its own line: either start-of-payload or preceded by \n.
            if (closeStart == 0 || trimmedRest[closeStart - 1] == '\n')
                trimmedRest = trimmedRest.Substring(0, closeStart).TrimEnd('\n', '\r');
        }
        return trimmedRest;
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
