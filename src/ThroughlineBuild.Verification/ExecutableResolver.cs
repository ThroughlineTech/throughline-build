namespace ThroughlineBuild.Verification;

/// <summary>
/// Resolves a check's <c>executable</c> to a launchable path on Windows.
///
/// Checks run with <c>UseShellExecute=false</c>, so the Win32 CreateProcess does NOT consult
/// PATHEXT - a bare name like <c>npm</c> fails to start because the real file on disk is
/// <c>npm.cmd</c> (and node, yarn, pnpm, tsc, vite, etc. are all <c>.cmd</c> shims). cmd.exe would
/// resolve it, but we deliberately do not shell out. So we replicate the PATHEXT lookup here:
/// for a bare name, walk PATH and try each PATHEXT extension, returning the first hit.
///
/// Config keeps the executable portable (<c>npm</c>, not <c>npm.cmd</c>) so the same op-doc-derived
/// config works on macOS/Linux; this resolver is the per-OS adaptation. On non-Windows, or for a
/// name that already has a directory separator or an extension, the input is returned unchanged.
/// Pure file I/O - no reflection (AOT-safe).
/// </summary>
public static class ExecutableResolver
{
    private static readonly string[] DefaultPathExt = { ".COM", ".EXE", ".BAT", ".CMD" };

    public static string Resolve(string executable)
    {
        if (!OperatingSystem.IsWindows()) return executable;
        if (string.IsNullOrWhiteSpace(executable)) return executable;

        // Already qualified (has a path) or already carries an extension: trust it verbatim.
        if (executable.Contains('/') || executable.Contains('\\')) return executable;
        if (Path.HasExtension(executable)) return executable;

        var pathExtRaw = Environment.GetEnvironmentVariable("PATHEXT");
        var exts = string.IsNullOrEmpty(pathExtRaw)
            ? DefaultPathExt
            : pathExtRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Match cmd.exe precedence: per directory, try each PATHEXT extension in order.
        foreach (var dir in pathDirs)
        {
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir, executable + ext); }
                catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }

        // No hit: return the original so Process.Start surfaces the real "not found" error.
        return executable;
    }
}
