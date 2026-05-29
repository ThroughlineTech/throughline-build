using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Copilot;

public class CopilotOptions
{
    public string ExecutablePath { get; init; } = "copilot";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, string> Sizes { get; init; } =
        new Dictionary<WorkerSize, string>
        {
            { WorkerSize.Small,  "claude-3.5-sonnet" },
            { WorkerSize.Medium, "gpt-4o" },
            { WorkerSize.Large,  "claude-3.7-sonnet" },
        };
}
