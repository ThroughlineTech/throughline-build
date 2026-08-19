using System.Diagnostics;

namespace ThroughlineBuild.Cli;

/// <summary>
/// The one repository-root resolver. Config, conductor, SOP manifest, and catalog stub
/// resolution all go through this type so a verb run inside a linked worktree stays bounded to
/// that repository and uses policy from its checked-out revision when present.
///
/// git owns the answer whenever it can give one: "rev-parse --show-toplevel" names the tree
/// the verb runs in, and "rev-parse --git-common-dir" leads to the main worktree. Tracked
/// .build/config.toml and .build/conductor.toml resolve from the current tree. The main worktree
/// remains the fallback for an older or partially-installed tree with no .build directory. A
/// linked worktree's .git is a file rather than a directory, which is exactly what hand-rolled
/// walks used to trip over.
///
/// Without git (no git on PATH, or a tree that is not a repository at all) resolution falls
/// back to a directory walk that stops at the first .git or .build it finds. Either way the
/// search for .build data is bounded by the repository: it can never climb past the worktree
/// and adopt an unrelated ancestor's configuration.
/// </summary>
internal sealed class RepositoryLayout
{
    private const string BuildDirectoryName = ".build";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private string? _buildDirectory;
    private bool _buildDirectoryResolved;

    private RepositoryLayout(string startDirectory, string worktreeRoot, string mainWorktreeRoot)
    {
        StartDirectory = startDirectory;
        WorktreeRoot = worktreeRoot;
        MainWorktreeRoot = mainWorktreeRoot;
    }

    /// <summary>Directory the verb was invoked from, fully qualified.</summary>
    public string StartDirectory { get; }

    /// <summary>Root of the tree the verb runs in. Tracked files resolve from here.</summary>
    public string WorktreeRoot { get; }

    /// <summary>Root of the clone's main worktree, used as the fallback for local .build state.</summary>
    public string MainWorktreeRoot { get; }

    public bool IsLinkedWorktree => !PathsEqual(WorktreeRoot, MainWorktreeRoot);

    /// <summary>
    /// Root that owns the resolved .build directory, or the main worktree when the repository has
    /// none yet (so a fresh install writes there).
    /// </summary>
    public string DataRoot =>
        FindBuildDirectory() is { } buildDirectory
            ? Path.GetDirectoryName(buildDirectory) ?? MainWorktreeRoot
            : MainWorktreeRoot;

    public static RepositoryLayout Resolve(string startDirectory)
    {
        var start = Normalize(Path.GetFullPath(startDirectory));
        var (worktreeRoot, mainWorktreeRoot) = ResolveRoots(start);
        return new RepositoryLayout(start, worktreeRoot, mainWorktreeRoot);
    }

    /// <summary>
    /// Nearest existing .build directory, searched from the start directory up to the worktree
    /// root and then, for a linked worktree, along the matching path in the main worktree.
    /// Null when the repository holds none. Resolved once per instance, so one invocation
    /// cannot answer this question two different ways.
    /// </summary>
    public string? FindBuildDirectory()
    {
        if (_buildDirectoryResolved)
            return _buildDirectory;

        foreach (var directory in SearchDirectories())
        {
            var candidate = Path.Combine(directory, BuildDirectoryName);
            if (Directory.Exists(candidate))
            {
                _buildDirectory = candidate;
                break;
            }
        }

        _buildDirectoryResolved = true;
        return _buildDirectory;
    }

    /// <summary>
    /// Absolute path of a .build file, or null when the repository has no .build
    /// directory or that directory does not hold the file. Fails closed rather than climbing
    /// past the repository into an unrelated ancestor.
    /// </summary>
    public string? FindBuildDataFile(string fileName)
    {
        var buildDirectory = FindBuildDirectory();
        if (buildDirectory is null)
            return null;

        var candidate = Path.Combine(buildDirectory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private IEnumerable<string> SearchDirectories()
    {
        foreach (var directory in Ancestors(StartDirectory, WorktreeRoot))
            yield return directory;

        if (!IsLinkedWorktree)
            yield break;

        // A linked worktree belongs to the same clone as the main worktree. When its current
        // revision has no tracked .build directory, fall back to local data in the main tree.
        // Preserve the relative position for repositories whose .build lives in a subdirectory.
        var relative = Path.GetRelativePath(WorktreeRoot, StartDirectory);
        var mapped = relative is "." || relative.StartsWith("..", StringComparison.Ordinal) ||
                     Path.IsPathRooted(relative)
            ? MainWorktreeRoot
            : Normalize(Path.GetFullPath(Path.Combine(MainWorktreeRoot, relative)));

        foreach (var directory in Ancestors(mapped, MainWorktreeRoot))
            yield return directory;
    }

    private static IEnumerable<string> Ancestors(string start, string boundary)
    {
        var stop = Normalize(boundary);
        var current = Normalize(start);
        if (!IsAtOrUnder(current, stop))
        {
            yield return stop;
            yield break;
        }

        while (true)
        {
            yield return current;
            if (PathsEqual(current, stop))
                yield break;

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                yield break;
            current = Normalize(parent);
        }
    }

    private static (string WorktreeRoot, string MainWorktreeRoot) ResolveRoots(string startDirectory)
    {
        if (Directory.Exists(startDirectory) &&
            TryReadGitRoots(startDirectory, out var worktreeRoot, out var commonDirectory))
        {
            return (worktreeRoot, MainWorktreeRootFrom(commonDirectory, worktreeRoot));
        }

        var walked = WalkForRoot(startDirectory);
        return (walked, walked);
    }

    private static bool TryReadGitRoots(
        string startDirectory,
        out string worktreeRoot,
        out string commonDirectory)
    {
        worktreeRoot = string.Empty;
        commonDirectory = string.Empty;

        string stdout;
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.WorkingDirectory = startDirectory;
            process.StartInfo.ArgumentList.Add("rev-parse");
            process.StartInfo.ArgumentList.Add("--show-toplevel");
            process.StartInfo.ArgumentList.Add("--git-common-dir");
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.Start();
            try { process.StandardInput.Close(); } catch { /* best effort */ }
            stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                return false;
        }
        catch (SystemException)
        {
            // No git on PATH, or it could not be started. The directory walk answers instead.
            return false;
        }

        var lines = stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count < 2)
            return false;

        worktreeRoot = Normalize(Path.GetFullPath(lines[0]));
        commonDirectory = Normalize(Path.GetFullPath(
            Path.IsPathFullyQualified(lines[1]) ? lines[1] : Path.Combine(startDirectory, lines[1])));
        return true;
    }

    private static string MainWorktreeRootFrom(string commonDirectory, string worktreeRoot)
    {
        // The common directory of any worktree of a clone is the main worktree's .git directory,
        // so its parent is the main worktree root. A bare or otherwise unusual layout has no such
        // parent tree; the current worktree is then the only root there is.
        if (!string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal))
            return worktreeRoot;

        var parent = Path.GetDirectoryName(commonDirectory);
        return parent is null || !Directory.Exists(parent) ? worktreeRoot : Normalize(parent);
    }

    private static string WalkForRoot(string startDirectory)
    {
        var current = startDirectory;
        while (true)
        {
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath) ||
                File.Exists(gitPath) ||
                Directory.Exists(Path.Combine(current, BuildDirectoryName)))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return startDirectory;
            current = Normalize(parent);
        }
    }

    private static bool IsAtOrUnder(string path, string ancestor)
    {
        if (PathsEqual(path, ancestor))
            return true;

        var relative = Path.GetRelativePath(ancestor, path);
        return relative != "." &&
            !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static bool PathsEqual(string left, string right) =>
        PathComparer.Equals(Normalize(left), Normalize(right));

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);
}
