using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Ownership record written alongside a run directory. It is advisory diagnostic
/// data only: stale detection is driven by the exclusively held <c>run.lock</c>
/// file, never by killing whatever process the recorded pid happens to name now.
/// </summary>
public sealed record ClaudeRunOwner(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("owner_pid")] int OwnerPid,
    [property: JsonPropertyName("owner_started_at_utc")] DateTimeOffset? OwnerStartedAtUtc,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc);

/// <summary>
/// Holds an exclusive lock on a run directory for the lifetime of one interactive
/// invocation. While the lease is open no other Throughline Build process can open
/// <c>run.lock</c> with <see cref="FileShare.None"/>, so the sweeper treats the
/// run as live. Disposing the lease releases the lock; the directory then looks
/// stale and is reclaimed on a later sweep (or by the owning invocation's cleanup).
/// </summary>
public sealed class ClaudeRunLease : IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const string LockFileName = "run.lock";
    public const string OwnerFileName = "owner.json";

    private FileStream? _lock;

    private ClaudeRunLease(FileStream lockStream, ClaudeRunOwner owner)
    {
        _lock = lockStream;
        Owner = owner;
    }

    public ClaudeRunOwner Owner { get; }

    public static ClaudeRunLease Acquire(ClaudeRunDirectory run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var lockPath = System.IO.Path.Combine(run.Path, LockFileName);
        // FileShare.None is the live/stale signal. We keep this handle open for the
        // whole run so a concurrent sweeper sees an IOException and leaves us alone.
        var stream = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var owner = new ClaudeRunOwner(
            CurrentSchemaVersion,
            Environment.ProcessId,
            TryGetCurrentProcessStartUtc(),
            Environment.MachineName,
            DateTimeOffset.UtcNow);
        try
        {
            File.WriteAllText(
                System.IO.Path.Combine(run.Path, OwnerFileName),
                JsonSerializer.Serialize(owner, ClaudeHookJsonContext.Default.ClaudeRunOwner));
        }
        catch
        {
            // The owner record is diagnostic only; the lock is what protects the run.
        }
        return new ClaudeRunLease(stream, owner);
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private static DateTimeOffset? TryGetCurrentProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return null; }
    }
}

/// <summary>
/// Reclaims orphaned interactive run directories left behind by a crashed parent.
/// A directory is reclaimed only when its <c>run.lock</c> can be opened
/// exclusively (proving no live run holds it) or, for legacy directories with no
/// lock file, when it is older than <paramref name="minimumAge"/>. The sweeper
/// never inspects or terminates processes, so a reused pid can never cause a kill.
/// </summary>
public static class ClaudeRunDirectorySweeper
{
    public static int SweepStaleRuns(
        string runParent,
        TimeSpan minimumAge,
        int maxDirectories = 256,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(runParent) || !Directory.Exists(runParent))
            return 0;

        var stamp = now ?? DateTimeOffset.UtcNow;
        var reclaimed = 0;
        var scanned = 0;
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(runParent); }
        catch { return 0; }

        foreach (var directory in directories)
        {
            if (scanned >= maxDirectories) break;
            scanned++;
            if (TryReclaim(directory, minimumAge, stamp)) reclaimed++;
        }

        return reclaimed;
    }

    private static bool TryReclaim(string directory, TimeSpan minimumAge, DateTimeOffset now)
    {
        var lockPath = System.IO.Path.Combine(directory, ClaudeRunLease.LockFileName);
        try
        {
            if (File.Exists(lockPath))
            {
                if (!IsLockFree(lockPath)) return false; // a live run still holds it
            }
            else if (now - new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero) < minimumAge)
            {
                return false; // legacy/partial directory that may still be in setup
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch
        {
            // A directory we cannot reclaim cleanly is left for the next sweep.
            return false;
        }
    }

    private static bool IsLockFree(string lockPath)
    {
        try
        {
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
