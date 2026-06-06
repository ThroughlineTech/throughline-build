using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Copilot;

public class CopilotOptions
{
    public string ExecutablePath { get; init; } = "copilot";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, ModelTier> Sizes { get; init; } =
        new Dictionary<WorkerSize, ModelTier>();
}
