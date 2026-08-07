using System.Diagnostics;
using System.Text;

namespace ThroughlineBuild.Cli;

/// <summary>
/// The canonical, language-neutral .gitignore entries a build-managed project should carry, and
/// the append-only merge logic that adds whatever is missing without disturbing existing content.
/// Entries cover the build tool's own artifacts (config holding the Plane token, transient brief,
/// event/session logs, feature worktrees, secrets) plus universal OS/editor noise - nothing
/// stack-specific (no node_modules, bin/obj, *.pyc, etc.); those belong to the project's own stack.
/// </summary>
public static class GitignoreManager
{
    public const string ManagedHeader = "# Throughline Build (managed by 'build setup')";

    public static readonly IReadOnlyList<string> RequiredEntries = new[]
    {
        // build tool artifacts. .build/config.toml is intentionally NOT here - it is tracked (see
        // TLB-627): it carries repository facts ([[review.checks]], [waves], [worktree], etc.) that
        // must travel with a clone, and the template default no longer writes a literal token into it.
        ".build/conductor.toml",
        ".build/sop-manifest.json",
        ".build/profile.json",
        ".build/invariants.toml",
        ".build/*.md",
        ".build/events/",
        ".build/sessions/",
        ".worktrees/",
        "secrets/",
        ".tmp/",
        // OS / editor noise (language-neutral)
        ".DS_Store",
        "Thumbs.db",
        ".vs/",
        ".idea/",
        "*.swp",
    };

    /// <summary>
    /// The required entries not already present in <paramref name="existing"/>. Matching is exact
    /// on the trimmed line; comment and blank lines in the existing file are ignored.
    /// </summary>
    public static IReadOnlyList<string> MissingEntries(string? existing)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var raw in existing.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                present.Add(line);
            }
        }
        return RequiredEntries.Where(e => !present.Contains(e)).ToList();
    }

    /// <summary>
    /// Returns new .gitignore content with the missing entries appended under the managed header,
    /// preserving every existing byte verbatim. Returns null when nothing is missing (no write
    /// needed). The header is only added once - re-running after new entries are introduced appends
    /// the new ones without a second header. Idempotent: a fully-covered file yields null.
    /// </summary>
    public static string? Merge(string? existing)
    {
        var missing = MissingEntries(existing);
        if (missing.Count == 0) return null;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(existing))
        {
            var normalized = existing.Replace("\r\n", "\n");
            sb.Append(normalized);
            if (!normalized.EndsWith('\n')) sb.Append('\n');
            sb.Append('\n'); // blank line separating prior content from the managed block
        }

        var headerPresent = existing is not null && existing.Contains(ManagedHeader, StringComparison.Ordinal);
        if (!headerPresent) sb.Append(ManagedHeader).Append('\n');
        foreach (var entry in missing) sb.Append(entry).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// Local-repository operations the setup command needs, abstracted for testability.
/// </summary>
public interface ILocalRepoOps
{
    bool IsGitRepository();
    void GitInit();
    string? ReadGitignore();
    void WriteGitignore(string content);
    /// <summary>Returns true when the repository has at least one commit (HEAD resolves).</summary>
    bool HasAnyCommits();
    /// <summary>Stages <paramref name="paths"/> and creates a commit with <paramref name="message"/>.</summary>
    void StageAndCommit(string[] paths, string message);
}

/// <summary>
/// Real <see cref="ILocalRepoOps"/> rooted at a working directory. <c>git init</c> is shelled
/// directly (UseShellExecute=false, no shell wrapper); gitignore is plain file I/O at the root.
/// </summary>
public sealed class FileSystemLocalRepoOps : ILocalRepoOps
{
    private readonly string _cwd;

    public FileSystemLocalRepoOps(string cwd) => _cwd = cwd;

    private string GitignorePath => Path.Combine(_cwd, ".gitignore");

    public bool IsGitRepository()
    {
        // A repo root has a .git directory; a linked worktree has a .git file. Either means "inside git".
        var dotGit = Path.Combine(_cwd, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    public void GitInit()
    {
        var psi = new ProcessStartInfo("git", "init")
        {
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start 'git init' (is git on PATH?)");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"'git init' failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    public string? ReadGitignore() =>
        File.Exists(GitignorePath) ? File.ReadAllText(GitignorePath) : null;

    // ASCII content, LF line endings - written verbatim so git sees stable bytes across platforms.
    public void WriteGitignore(string content) =>
        File.WriteAllText(GitignorePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public bool HasAnyCommits()
    {
        var psi = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start 'git rev-parse HEAD' (is git on PATH?)");
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }

    public void StageAndCommit(string[] paths, string message)
    {
        // Stage the specified paths (UseShellExecute=false, ArgumentList handles quoting).
        var psiAdd = new ProcessStartInfo("git")
        {
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psiAdd.ArgumentList.Add("add");
        psiAdd.ArgumentList.Add("--");
        foreach (var p in paths) psiAdd.ArgumentList.Add(p);

        using var addProc = Process.Start(psiAdd)
            ?? throw new InvalidOperationException("failed to start 'git add' (is git on PATH?)");
        var addStderr = addProc.StandardError.ReadToEnd();
        addProc.WaitForExit();
        if (addProc.ExitCode != 0)
            throw new InvalidOperationException($"'git add' failed (exit {addProc.ExitCode}): {addStderr.Trim()}");

        // Commit with ArgumentList so the message is quoted correctly on all platforms.
        var psiCommit = new ProcessStartInfo("git")
        {
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psiCommit.ArgumentList.Add("commit");
        psiCommit.ArgumentList.Add("-m");
        psiCommit.ArgumentList.Add(message);

        using var commitProc = Process.Start(psiCommit)
            ?? throw new InvalidOperationException("failed to start 'git commit' (is git on PATH?)");
        var commitStderr = commitProc.StandardError.ReadToEnd();
        commitProc.WaitForExit();
        if (commitProc.ExitCode != 0)
            throw new InvalidOperationException($"'git commit' failed (exit {commitProc.ExitCode}): {commitStderr.Trim()}");
    }
}
