using System.Runtime.InteropServices;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Canonicalizes a filesystem path to the same form Claude Code records as the
/// transcript <c>cwd</c>. On Unix this means resolving symlinks: macOS exposes
/// <c>/var</c> as a symlink to <c>/private/var</c>, so <c>Path.GetTempPath()</c>
/// hands back an UNRESOLVED <c>/var/folders/...</c> worktree while claude records
/// the RESOLVED <c>/private/var/folders/...</c>. <see cref="Path.GetFullPath(string)"/>
/// does not resolve symlinks, so the turn-detector's directory match would never
/// fire and the worker would hang until timeout. We resolve via libc
/// <c>realpath</c> so our spawn cwd, trust key, and turn-match all use claude's
/// own canonical form.
/// </summary>
internal static class ClaudeRealPath
{
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);

    /// <summary>
    /// Returns the canonical absolute path. On Windows (temp has no parent
    /// symlinks) this is just <see cref="Path.GetFullPath(string)"/>. On Unix it
    /// resolves all symlinks via libc <c>realpath</c>, falling back to
    /// <see cref="Path.GetFullPath(string)"/> if realpath fails (e.g. the path
    /// does not exist yet). The path is expected to exist by the time this runs.
    /// </summary>
    public static string Resolve(string path)
    {
        if (OperatingSystem.IsWindows())
            return Path.GetFullPath(path);

        // realpath mallocs the result when the second arg is NULL; we own the free.
        IntPtr result = IntPtr.Zero;
        try
        {
            result = realpath(path, IntPtr.Zero);
            if (result == IntPtr.Zero)
                return Path.GetFullPath(path);
            return Marshal.PtrToStringUTF8(result) ?? Path.GetFullPath(path);
        }
        catch
        {
            return Path.GetFullPath(path);
        }
        finally
        {
            if (result != IntPtr.Zero) free(result);
        }
    }
}
