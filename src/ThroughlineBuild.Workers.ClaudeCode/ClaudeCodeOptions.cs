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
    // Default transport. The product default is interactive-hook after the Stage 07 cutover; the
    // load-bearing default lives in the config loader's omitted-value branch (Config.cs), which
    // WorkerAgentBuilder always passes here explicitly. This type-level default stays Print because it
    // only governs directly-constructed options (tests, the print transport itself), not config loading.
    public ClaudeCodeTransport Transport { get; init; } = ClaudeCodeTransport.Print;
    // When true (default), pass --dangerously-skip-permissions to the CLI so the
    // headless --print run does not block on the interactive approval gate. Set
    // false from config to opt back into the gate (rarely useful for workers,
    // but kept as an escape hatch).
    public bool BypassPermissions { get; init; } = true;
    // Interactive-hook runs do not rely on Claude's Stop hook for correctness anymore:
    // completion is synthesized from the persisted transcript. Keeping the hook enabled
    // preserves the build CLI's fast-path/debug behavior, while reusable library hosts can
    // disable it when they do not expose an "internal claude-stop-hook" command.
    public bool EnableStopHook { get; init; } = true;
    // Optional command prefix for the Claude Stop hook in interactive-hook mode. When null
    // and EnableStopHook is true, the transport infers the current build executable. Library
    // consumers that do host the bridge can pass their own executable/argument vector here.
    public IReadOnlyList<string>? StopHookCommandPrefix { get; init; } = null;
}
