using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class MainWorktreeLockTests
{
    [Fact]
    public async Task SamePath_SecondCallerBlocked_UntilFirstReleases()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var gate1Enter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate1Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = MainWorktreeLock.WithLockAsync(path, async ct =>
        {
            gate1Enter.SetResult(true);
            await gate1Release.Task;
        }, CancellationToken.None);

        await gate1Enter.Task;

        var task2 = MainWorktreeLock.WithLockAsync(path, ct => Task.CompletedTask, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(task2.IsCompleted);

        gate1Release.SetResult(true);
        await task1;
        await task2;
    }

    [Fact]
    public async Task DifferentPaths_BothProceedConcurrently()
    {
        var path1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var gate1Enter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate1Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2Enter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = MainWorktreeLock.WithLockAsync(path1, async ct =>
        {
            gate1Enter.SetResult(true);
            await gate1Release.Task;
        }, CancellationToken.None);

        var task2 = MainWorktreeLock.WithLockAsync(path2, async ct =>
        {
            gate2Enter.SetResult(true);
            await gate2Release.Task;
        }, CancellationToken.None);

        try
        {
            await Task.WhenAll(gate1Enter.Task, gate2Enter.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            Assert.Fail("Both callers did not reach their gates within 2 seconds - lock may be serializing independent paths.");
        }

        gate1Release.SetResult(true);
        gate2Release.SetResult(true);
        await Task.WhenAll(task1, task2);
    }

    [Fact]
    public async Task Cancellation_QueuedCallerProceedsAfterHolderCancels()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var cts1 = new CancellationTokenSource();
        var gate1Enter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = MainWorktreeLock.WithLockAsync(path, async ct =>
        {
            gate1Enter.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        }, cts1.Token);

        await gate1Enter.Task;

        var task2 = MainWorktreeLock.WithLockAsync(path, ct => Task.CompletedTask, CancellationToken.None);

        cts1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
        await task2.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>Models ShipPhase's two separate WithLockAsync windows (fetch and ff-merge) on the same path.</summary>
    [Fact]
    public async Task ShipWindows_FetchAndMergeWindows_SerializeOnSamePath()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var gate1Enter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate1Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = MainWorktreeLock.WithLockAsync(path, async ct =>
        {
            gate1Enter.SetResult(true);
            await gate1Release.Task;
        }, CancellationToken.None);

        await gate1Enter.Task;

        var task2 = MainWorktreeLock.WithLockAsync(path, ct => Task.CompletedTask, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(task2.IsCompleted);

        gate1Release.SetResult(true);
        await task1;
        await task2;
    }
}
