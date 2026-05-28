using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Codex;

public class CodexOptions
{
    public string ExecutablePath { get; init; } = "codex";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, string> Sizes { get; init; } =
        new Dictionary<WorkerSize, string>();
}
