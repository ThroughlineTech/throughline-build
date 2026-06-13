using System.Security.Cryptography;
using System.Text;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Everything a terminal host needs to launch one interactive Claude process.
/// Arguments are an explicit vector; no host builds them through shell string
/// concatenation.
/// </summary>
internal sealed record InteractiveClaudeLaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

/// <summary>
/// A live interactive Claude process tree. Implementations own platform-specific
/// terminal resources (Windows ConPTY, a Unix PTY) and must contain and reclaim
/// the entire descendant tree on <see cref="TerminateAsync"/> and disposal.
/// </summary>
internal interface IInteractiveClaudeProcess : IAsyncDisposable
{
    Task<int> ExitTask { get; }

    /// <summary>
    /// Bounded graceful-then-forced shutdown of the whole process tree. Signals a
    /// graceful exit first, waits a bounded grace period, then escalates to a
    /// forced tree kill. Throws when the tree could not be terminated.
    /// </summary>
    Task TerminateAsync(CancellationToken cancellationToken);
}

internal interface IInteractiveClaudeProcessLauncher
{
    IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec);
}

/// <summary>
/// Selects the terminal host for the current platform. Keeps the platform branch
/// in one place so the transport, run store, and result parsing never reference a
/// platform-specific host directly.
/// </summary>
internal static class InteractiveClaudeProcessLauncherFactory
{
    public static IInteractiveClaudeProcessLauncher Create() =>
        OperatingSystem.IsWindows()
            ? new WindowsConPtyClaudeProcessLauncher()
            : new UnixInteractiveClaudeProcessLauncher();
}

/// <summary>
/// Shared bounded shutdown escalation used by every terminal host: signal a
/// graceful exit, wait the grace period, then force-kill the tree and wait again.
/// Pure with respect to the platform - the host supplies the native signal and
/// kill callbacks - so the escalation policy itself is deterministically testable.
/// </summary>
internal static class ProcessShutdownSequence
{
    /// <returns>
    /// <c>null</c> when the tree has exited; otherwise an actionable message. A
    /// genuine failure inside <paramref name="forceKill"/> propagates as an exception.
    /// </returns>
    public static async Task<string?> RunAsync(
        Task<int> exitTask,
        Func<bool> hasExited,
        Func<Task> signalGraceful,
        Func<Task> forceKill,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout)
    {
        if (hasExited()) return null;

        try { await signalGraceful().ConfigureAwait(false); }
        catch { /* graceful signalling is best-effort; escalation still runs */ }

        if (await WaitForExitAsync(exitTask, gracefulTimeout).ConfigureAwait(false))
            return null;

        await forceKill().ConfigureAwait(false);

        if (await WaitForExitAsync(exitTask, forcedTimeout).ConfigureAwait(false))
            return null;

        return "the process tree did not exit after graceful shutdown and forced termination";
    }

    private static async Task<bool> WaitForExitAsync(Task<int> exitTask, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return exitTask.IsCompleted;
        try
        {
            await exitTask.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>
/// A per-worktree exclusive lock that prevents two interactive runs from racing on
/// the shared <c>.build/brief.md</c> path inside one working tree. The lock lives
/// in the per-user temp tree keyed by the worktree's full path, so independent
/// worktrees never contend and the repository is never polluted with a lock file.
/// </summary>
internal sealed class InteractiveClaudeWorktreeLock : IDisposable
{
    private FileStream? _stream;

    private InteractiveClaudeWorktreeLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    public static string PathFor(string workingDirectory)
    {
        var full = System.IO.Path.GetFullPath(workingDirectory);
        // Windows paths are case-insensitive; fold case so two spellings of the
        // same worktree map to the same lock.
        var key = OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "latticeflow-claude-locks");
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, hash + ".lock");
    }

    /// <returns><c>null</c> when another live run already holds this worktree.</returns>
    public static InteractiveClaudeWorktreeLock? TryAcquire(string workingDirectory)
    {
        var path = PathFor(workingDirectory);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InteractiveClaudeWorktreeLock(stream, path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
