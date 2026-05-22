using System.Text.Json;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal static class WorkerResultParser
{
    private static readonly JsonSerializerOptions s_opts = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static WorkerResult? TryParse(string stdout)
    {
        // Scan for "WORKER_RESULT" marker line, then read next non-empty line as JSON
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
                        var dto = JsonSerializer.Deserialize<WorkerResultDto>(json, s_opts);
                        if (dto is null) return null;
                        IReadOnlyList<string> files = dto.FilesChanged ?? new List<string>();
                        IReadOnlyDictionary<string, object> meta = dto.Metadata ?? new Dictionary<string, object>();
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

    // Plain DTO with concrete types to avoid interface deserialization issues
    private sealed class WorkerResultDto
    {
        public Status Status { get; set; }
        public string? Summary { get; set; }
        public List<string>? FilesChanged { get; set; }
        public string? FailureReason { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }
}
