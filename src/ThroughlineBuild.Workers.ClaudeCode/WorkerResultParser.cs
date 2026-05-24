using System.Text.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal static class WorkerResultParser
{
    internal static WorkerResult? TryParse(string stdout)
    {
        // Scan for "WORKER_RESULT" marker line, then read next non-empty line as JSON.
        // Uses the source-gen overload (ClaudeCodeJsonContext) so deserialization works
        // under PublishAot=true where reflection-based JsonSerializer.Deserialize<T>
        // throws NotSupportedException. See docs/throughline-build-architecture.md,
        // section "AOT serialization traps".
        var lines = stdout.Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].Trim() == "WORKER_RESULT")
            {
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var json = lines[j].Trim();
                    if (string.IsNullOrEmpty(json)) continue;
                    try
                    {
                        var dto = JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);
                        if (dto is null) return null;
                        IReadOnlyList<string> files = dto.FilesChanged ?? new List<string>();
                        // Metadata values are JsonElement; downstream TryGetString helpers already handle
                        // both string and JsonElement, so no further unwrapping is needed here.
                        IReadOnlyDictionary<string, object> meta = dto.Metadata is not null
                            ? dto.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
                            : new Dictionary<string, object>();
                        return new WorkerResult(
                            dto.Status,
                            dto.Summary ?? string.Empty,
                            files,
                            dto.FailureReason,
                            meta);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }
        return null;
    }
}
