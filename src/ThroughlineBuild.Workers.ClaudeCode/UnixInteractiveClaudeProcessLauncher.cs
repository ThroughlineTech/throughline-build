namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Unix arm of the interactive terminal host abstraction.
///
/// Stage 01 proved the interactive Stop-hook contract and the need for terminal
/// semantics on Windows only; there is no equivalent Unix evidence yet, and this
/// development host cannot validate a native Unix PTY launcher. Rather than ship
/// unverified fork/PTY interop that would undercut this stage's own reliability
/// bar, the Unix arm fails fast with an actionable message. The transport never
/// silently falls back to <c>--print</c>; the operator selects <c>print</c>
/// explicitly when running on a Unix host.
///
/// This type is the single drop-in extension point: a future stage that produces
/// Stage-01-equivalent Unix evidence implements a PTY-backed
/// <see cref="IInteractiveClaudeProcess"/> here (graceful SIGTERM to the child's
/// process group, then SIGKILL, mirroring the Windows job-object escalation) and
/// returns it from <see cref="Launch"/>. Nothing in the transport, run store, or
/// result parsing changes.
/// </summary>
internal sealed class UnixInteractiveClaudeProcessLauncher : IInteractiveClaudeProcessLauncher
{
    public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec) =>
        throw new PlatformNotSupportedException(
            "The interactive-hook transport has no validated terminal host on this platform yet; " +
            "it is proven on Windows (ConPTY). Set [workers.claude-code] transport = \"print\" on this host, " +
            "or supply Stage-01-equivalent Unix PTY evidence to enable a Unix host.");
}
