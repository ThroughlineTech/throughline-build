using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ThroughlineBuild.Helpers;

public static class MainWorktreeLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.Ordinal);

    public static async Task WithLockAsync(string mainWorktreePath, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(mainWorktreePath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            normalizedPath = normalizedPath.ToLowerInvariant();

        var semaphore = Locks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
