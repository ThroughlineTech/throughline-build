using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

public enum ClaudeCodeTransport
{
    Print,
    InteractiveHook,
}

public class ClaudeCodeOptions
{
    public string ExecutablePath { get; init; } = "claude";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, ModelTier> Sizes { get; init; } =
        new Dictionary<WorkerSize, ModelTier>();
    public ClaudeCodeTransport Transport { get; init; } = ClaudeCodeTransport.Print;
    // When true (default), pass --dangerously-skip-permissions to the CLI so the
    // headless --print run does not block on the interactive approval gate. Set
    // false from config to opt back into the gate (rarely useful for workers,
    // but kept as an escape hatch).
    public bool BypassPermissions { get; init; } = true;
}
