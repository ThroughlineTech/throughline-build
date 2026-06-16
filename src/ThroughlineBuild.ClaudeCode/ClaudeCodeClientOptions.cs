using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using WorkerClaudeCodeOptions = ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeOptions;
using WorkerClaudeCodeTransport = ThroughlineBuild.Workers.ClaudeCode.ClaudeCodeTransport;

namespace ThroughlineBuild.ClaudeCode;

/// <summary>
/// Process-level Claude Code settings shared by every run issued by a
/// <see cref="ClaudeCodeClient"/>.
/// </summary>
public sealed class ClaudeCodeClientOptions
{
    public string ExecutablePath { get; init; } = "claude";
    public ClaudeCodeTransportMode Transport { get; init; } = ClaudeCodeTransportMode.InteractiveHook;
    public bool BypassPermissions { get; init; } = true;
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; }
    public IReadOnlyDictionary<WorkerSize, ModelTier> Sizes { get; init; } =
        new Dictionary<WorkerSize, ModelTier>();

    /// <summary>
    /// Enables Claude's Stop hook fast-path. Library hosts default this to false because
    /// they usually do not expose the ThroughlineBuild CLI's internal hook command.
    /// Transcript-based completion remains active either way.
    /// </summary>
    public bool EnableStopHook { get; init; } = false;

    /// <summary>
    /// Command prefix for hosts that choose to expose
    /// <see cref="ClaudeStopHookBridge.RunAsync"/> from their own executable.
    /// </summary>
    public IReadOnlyList<string>? StopHookCommandPrefix { get; init; }

    internal WorkerClaudeCodeOptions ToWorkerOptions() => new()
    {
        ExecutablePath = ExecutablePath,
        Transport = ToWorkerTransport(Transport),
        BypassPermissions = BypassPermissions,
        ExtraArgs = ExtraArgs,
        MaxOutputTokens = MaxOutputTokens,
        Sizes = Sizes,
        EnableStopHook = EnableStopHook,
        StopHookCommandPrefix = StopHookCommandPrefix,
    };

    internal static WorkerClaudeCodeTransport ToWorkerTransport(ClaudeCodeTransportMode transport) =>
        transport == ClaudeCodeTransportMode.Print
            ? WorkerClaudeCodeTransport.Print
            : WorkerClaudeCodeTransport.InteractiveHook;
}
