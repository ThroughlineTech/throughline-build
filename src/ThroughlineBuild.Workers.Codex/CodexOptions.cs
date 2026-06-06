using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Codex;

public class CodexOptions
{
    public string ExecutablePath { get; init; } = "codex";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, ModelTier> Sizes { get; init; } =
        new Dictionary<WorkerSize, ModelTier>();
    // When true (default), pass the codex full-bypass flag so the headless run
    // does not block on the interactive approval gate or Windows sandbox.
    // Set false from config to opt back into the gate.
    public bool BypassPermissions { get; init; } = true;
}
