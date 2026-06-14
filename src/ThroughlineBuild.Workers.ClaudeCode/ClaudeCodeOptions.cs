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
    // Default for directly-constructed options. The PRODUCT default for loaded configuration is
    // interactive-hook (set by the config loader's omitted-value default and the generated config
    // template as of the Stage 07 cutover). This type default stays Print so directly-constructed
    // options - tests and the print transport itself - keep the legacy path unless they opt in;
    // WorkerAgentBuilder always passes the loaded transport explicitly, so production is unaffected.
    public ClaudeCodeTransport Transport { get; init; } = ClaudeCodeTransport.Print;
    // When true (default), pass --dangerously-skip-permissions to the CLI so the
    // headless --print run does not block on the interactive approval gate. Set
    // false from config to opt back into the gate (rarely useful for workers,
    // but kept as an escape hatch).
    public bool BypassPermissions { get; init; } = true;
}
