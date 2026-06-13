using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ProcessShutdownSequenceTests
{
    [Fact]
    public async Task GracefulExit_DoesNotForceKill()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = false;

        var error = await ProcessShutdownSequence.RunAsync(
            exit.Task,
            () => exit.Task.IsCompleted,
            () => { exit.TrySetResult(0); return Task.CompletedTask; }, // graceful signal works
            () => { forced = true; return Task.CompletedTask; },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        Assert.Null(error);
        Assert.False(forced);
    }

    [Fact]
    public async Task GracefulIgnored_EscalatesToForcedKill()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = false;

        var error = await ProcessShutdownSequence.RunAsync(
            exit.Task,
            () => exit.Task.IsCompleted,
            () => Task.CompletedTask,                                 // graceful does nothing
            () => { forced = true; exit.TrySetResult(137); return Task.CompletedTask; },
            TimeSpan.Zero,                                            // skip the grace wait
            TimeSpan.FromSeconds(5));

        Assert.Null(error);
        Assert.True(forced);
    }

    [Fact]
    public async Task ForcedKillThatNeverExits_ReportsActionableError()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var error = await ProcessShutdownSequence.RunAsync(
            exit.Task,
            () => exit.Task.IsCompleted,
            () => Task.CompletedTask,
            () => Task.CompletedTask,                                 // kill is a no-op; process never exits
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50));

        Assert.NotNull(error);
    }

    [Fact]
    public async Task ForcedKillThrows_Propagates()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessShutdownSequence.RunAsync(
            exit.Task,
            () => false,
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("kill failed"),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AlreadyExited_SkipsBothSignals()
    {
        var graceful = false;
        var forced = false;

        var error = await ProcessShutdownSequence.RunAsync(
            Task.FromResult(0),
            () => true,
            () => { graceful = true; return Task.CompletedTask; },
            () => { forced = true; return Task.CompletedTask; },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        Assert.Null(error);
        Assert.False(graceful);
        Assert.False(forced);
    }
}

public sealed class InteractiveClaudeWorktreeLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lattice worktree lock {Guid.NewGuid():N}");

    [Fact]
    public void PathFor_IsStablePerWorktreeAndDistinctAcrossWorktrees()
    {
        var a = Path.Combine(_root, "alpha");
        var b = Path.Combine(_root, "beta");

        Assert.Equal(InteractiveClaudeWorktreeLock.PathFor(a), InteractiveClaudeWorktreeLock.PathFor(a));
        Assert.NotEqual(InteractiveClaudeWorktreeLock.PathFor(a), InteractiveClaudeWorktreeLock.PathFor(b));
    }

    [Fact]
    public void TryAcquire_IsExclusivePerWorktreeAndReleasable()
    {
        var worktree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(worktree);

        var first = InteractiveClaudeWorktreeLock.TryAcquire(worktree);
        Assert.NotNull(first);
        Assert.Null(InteractiveClaudeWorktreeLock.TryAcquire(worktree)); // collision while held

        first!.Dispose();
        using var third = InteractiveClaudeWorktreeLock.TryAcquire(worktree);
        Assert.NotNull(third); // reacquirable once released
    }

    [Fact]
    public void TryAcquire_IndependentWorktreesDoNotContend()
    {
        var a = Path.Combine(_root, "wt-a");
        var b = Path.Combine(_root, "wt-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        using var lockA = InteractiveClaudeWorktreeLock.TryAcquire(a);
        using var lockB = InteractiveClaudeWorktreeLock.TryAcquire(b);

        Assert.NotNull(lockA);
        Assert.NotNull(lockB);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
