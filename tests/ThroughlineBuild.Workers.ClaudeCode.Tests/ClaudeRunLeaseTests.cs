using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ClaudeRunLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lattice lease tests {Guid.NewGuid():N}");

    private ClaudeRunDirectory NewRun()
    {
        Directory.CreateDirectory(_root);
        var runId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, runId);
        Directory.CreateDirectory(path);
        return ClaudeRunDirectory.Open(path, runId);
    }

    [Fact]
    public void Acquire_HoldsExclusiveLockAndWritesOwnerRecord()
    {
        var run = NewRun();
        using var lease = ClaudeRunLease.Acquire(run);

        var ownerPath = Path.Combine(run.Path, ClaudeRunLease.OwnerFileName);
        Assert.True(File.Exists(ownerPath));
        Assert.Contains(Environment.ProcessId.ToString(), File.ReadAllText(ownerPath));
        Assert.Equal(Environment.ProcessId, lease.Owner.OwnerPid);

        // The lock is the live/stale signal: nobody else can open it exclusively.
        Assert.Throws<IOException>(() =>
            new FileStream(Path.Combine(run.Path, ClaudeRunLease.LockFileName),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
    }

    [Fact]
    public void Sweep_ReclaimsRunWhoseLeaseWasReleased()
    {
        var run = NewRun();
        ClaudeRunLease.Acquire(run).Dispose(); // a parent that crashed then released the lock

        var reclaimed = ClaudeRunDirectorySweeper.SweepStaleRuns(_root, TimeSpan.FromHours(1));

        Assert.Equal(1, reclaimed);
        Assert.False(Directory.Exists(run.Path));
    }

    [Fact]
    public void Sweep_SkipsRunWithLiveLease()
    {
        var run = NewRun();
        using var lease = ClaudeRunLease.Acquire(run); // still live

        var reclaimed = ClaudeRunDirectorySweeper.SweepStaleRuns(_root, TimeSpan.FromHours(1));

        Assert.Equal(0, reclaimed);
        Assert.True(Directory.Exists(run.Path));
    }

    [Fact]
    public void Sweep_AgeGatesLocklessDirectories()
    {
        var orphan = NewRun(); // a directory created but never leased

        // Younger than the bound: left alone.
        Assert.Equal(0, ClaudeRunDirectorySweeper.SweepStaleRuns(_root, TimeSpan.FromHours(1)));
        Assert.True(Directory.Exists(orphan.Path));

        // Older than the bound (zero): reclaimed.
        Assert.Equal(1, ClaudeRunDirectorySweeper.SweepStaleRuns(_root, TimeSpan.Zero));
        Assert.False(Directory.Exists(orphan.Path));
    }

    [Fact]
    public void Sweep_OnMissingParent_IsNoOp()
    {
        Assert.Equal(0, ClaudeRunDirectorySweeper.SweepStaleRuns(Path.Combine(_root, "does-not-exist"), TimeSpan.FromHours(1)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
