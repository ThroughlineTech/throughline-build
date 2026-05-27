using System.Diagnostics;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Git;

public sealed class ProcessGitClient : IGitClient
{
    private readonly string? _workingDirectory;

    public ProcessGitClient(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
    }

    public async Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse {refspec}")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"git rev-parse {refspec} failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        return stdout.Trim();
    }

    public async Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
    {
        var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--porcelain");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"git worktree list failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        return ParseWorktreeList(stdout);
    }

    private static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string output)
    {
        var result = new List<WorktreeInfo>();
        string? currentPath = null;
        string? currentSha = null;
        string? currentBranch = null;
        bool isLocked = false;
        bool isPrunable = false;

        void FlushCurrent()
        {
            if (currentPath is not null)
            {
                result.Add(new WorktreeInfo(
                    currentPath,
                    currentBranch ?? "",
                    currentSha ?? "",
                    isLocked,
                    isPrunable));
                currentPath = null;
                currentSha = null;
                currentBranch = null;
                isLocked = false;
                isPrunable = false;
            }
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                FlushCurrent();
                continue;
            }
            if (line.StartsWith("worktree "))
                currentPath = line.Substring("worktree ".Length);
            else if (line.StartsWith("HEAD "))
                currentSha = line.Substring("HEAD ".Length);
            else if (line.StartsWith("branch refs/heads/"))
                currentBranch = line.Substring("branch refs/heads/".Length);
            else if (line == "detached")
                currentBranch = "";
            else if (line.StartsWith("locked"))
                isLocked = true;
            else if (line.StartsWith("prunable"))
                isPrunable = true;
        }

        FlushCurrent();
        return result;
    }

    public async Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct)
    {
        try
        {
            var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = wd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("branch");
            psi.ArgumentList.Add("--list");
            psi.ArgumentList.Add(pattern);
            psi.ArgumentList.Add("--no-merged");
            psi.ArgumentList.Add(baseBranch);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return Array.Empty<string>();

            var result = new List<string>();
            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.StartsWith("* "))
                    line = line.Substring(2).Trim();
                if (line.Length == 0) continue;
                result.Add(line);
            }
            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
    {
        var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("remove");
        if (force)
            psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            return new WorktreeRemoveResult(false, stderr.Trim());
        return new WorktreeRemoveResult(true, null);
    }

    public async Task<WorktreeCreateResult> CreateWorktreeAsync(
        string worktreePath,
        string newBranch,
        string fromRef,
        string mainWorktreePath,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = mainWorktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(newBranch);
            psi.ArgumentList.Add(worktreePath);
            psi.ArgumentList.Add(fromRef);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return new WorktreeCreateResult(false, stderr.Trim(), null);
            return new WorktreeCreateResult(true, null, Path.GetFullPath(worktreePath));
        }
        catch (Exception ex)
        {
            return new WorktreeCreateResult(false, ex.Message, null);
        }
    }

    // Returns the HEAD SHA of the given worktree, or empty string on failure.
    // Does not throw on git-level failure; callers check string.Length == 40 to detect failure.
    public async Task<string> HeadShaAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return string.Empty;
            return stdout.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<GitDiff> DiffAsync(
        string fromRef,
        string toRef,
        string mainWorktreePath,
        bool includePatchContent,
        CancellationToken ct)
    {
        // Step 1: git diff --name-status fromRef...toRef
        var nameStatusOutput = await RunGitAsync(
            mainWorktreePath,
            new[] { "diff", "--name-status", $"{fromRef}...{toRef}" },
            ct).ConfigureAwait(false);

        var nameStatusEntries = ParseNameStatus(nameStatusOutput);
        if (nameStatusEntries.Count == 0)
            return new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>());

        // Step 2: git diff --numstat fromRef...toRef
        var numstatOutput = await RunGitAsync(
            mainWorktreePath,
            new[] { "diff", "--numstat", $"{fromRef}...{toRef}" },
            ct).ConfigureAwait(false);

        var numstatMap = ParseNumstat(numstatOutput);

        // Step 3: build entries, optionally fetching patch content
        var entries = new List<DiffEntry>(nameStatusEntries.Count);
        foreach (var (path, kind, oldPath) in nameStatusEntries)
        {
            numstatMap.TryGetValue(path, out var counts);
            string? patchContent = null;
            if (includePatchContent)
            {
                patchContent = await FetchPatchAsync(
                    mainWorktreePath, fromRef, toRef, path, oldPath, ct).ConfigureAwait(false);
            }
            entries.Add(new DiffEntry(
                path,
                kind,
                oldPath,
                counts.Added,
                counts.Removed,
                patchContent));
        }

        return new GitDiff(fromRef, toRef, entries);
    }

    private static List<(string Path, DiffKind Kind, string? OldPath)> ParseNameStatus(string output)
    {
        var result = new List<(string, DiffKind, string?)>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split('\t');
            if (parts.Length < 2) continue;

            var statusCode = parts[0];
            DiffKind kind;
            string path;
            string? oldPath = null;

            if (statusCode.StartsWith("R"))
            {
                kind = DiffKind.Renamed;
                if (parts.Length >= 3)
                {
                    oldPath = parts[1];
                    path = parts[2];
                }
                else
                {
                    // Unexpected format; skip
                    continue;
                }
            }
            else
            {
                path = parts[1];
                kind = statusCode switch
                {
                    "A" => DiffKind.Added,
                    "D" => DiffKind.Deleted,
                    _ => DiffKind.Modified
                };
            }

            result.Add((path, kind, oldPath));
        }
        return result;
    }

    // Returns a dictionary keyed by the "new" path (or only path for non-renames).
    // For renames git --numstat emits either:
    //   added  removed  {old => new}
    // or in older git versions:
    //   added  removed  old  new
    // We normalise both forms to the new path as the key.
    private static Dictionary<string, (int Added, int Removed)> ParseNumstat(string output)
    {
        var result = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var addedStr = parts[0];
            var removedStr = parts[1];

            int added = addedStr == "-" ? 0 : (int.TryParse(addedStr, out var a) ? a : 0);
            int removed = removedStr == "-" ? 0 : (int.TryParse(removedStr, out var r) ? r : 0);

            if (parts.Length == 3)
            {
                // Standard or curly-brace rename form: "{old => new}" or just "path"
                var pathField = parts[2];
                if (pathField.Contains('{') && pathField.Contains("=>"))
                {
                    // Parse {old => new} form embedded in a path like "dir/{old => new}"
                    var key = ResolveCurlyBraceRename(pathField);
                    result[key] = (added, removed);
                }
                else
                {
                    result[pathField] = (added, removed);
                }
            }
            else if (parts.Length >= 4)
            {
                // Old-style rename: added  removed  oldpath  newpath
                result[parts[3]] = (added, removed);
            }
        }
        return result;
    }

    // Resolves a curly-brace rename like "dir/{old => new}/file" or "{old => new}"
    // to the "new" path.
    private static string ResolveCurlyBraceRename(string path)
    {
        var openBrace = path.IndexOf('{');
        var closeBrace = path.IndexOf('}');
        if (openBrace < 0 || closeBrace < 0 || closeBrace <= openBrace)
            return path;

        var prefix = path.Substring(0, openBrace);
        var suffix = path.Substring(closeBrace + 1);
        var inner = path.Substring(openBrace + 1, closeBrace - openBrace - 1);
        var arrow = inner.IndexOf("=>", StringComparison.Ordinal);
        if (arrow < 0)
            return path;

        var newPart = inner.Substring(arrow + 2).Trim();
        return (prefix + newPart + suffix).Replace("//", "/").TrimEnd('/');
    }

    // Fetches the patch for a single file. Returns null if the patch exceeds 100 KB.
    private static async Task<string?> FetchPatchAsync(
        string mainWorktreePath,
        string fromRef,
        string toRef,
        string newPath,
        string? oldPath,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = mainWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add($"{fromRef}...{toRef}");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(newPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        const int PatchSizeCap = 102400;
        var buffer = new char[4096];
        var sb = new System.Text.StringBuilder();
        bool capped = false;

        using var reader = proc.StandardOutput;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            if (!capped)
            {
                sb.Append(buffer, 0, read);
                if (sb.Length > PatchSizeCap)
                    capped = true;
            }
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (capped)
            return null;

        return sb.Length == 0 ? null : sb.ToString();
    }

    public async Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = mainWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("fetch");
        psi.ArgumentList.Add(remote);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            return new GitOpResult(false, stderr.Trim());
        return new GitOpResult(true, null);
    }

    public async Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = featureWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("rebase");
        psi.ArgumentList.Add(ontoRef);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode == 0)
            return new RebaseResult(true, false, Array.Empty<string>(), null);

        // Non-zero: check for unmerged paths (conflicts)
        var conflictingPaths = await GetUnmergedPathsAsync(featureWorktreePath, ct).ConfigureAwait(false);
        bool hadConflicts = conflictingPaths.Count > 0;
        return new RebaseResult(false, hadConflicts, conflictingPaths, stderr.Trim());
    }

    private static async Task<IReadOnlyList<string>> GetUnmergedPathsAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--name-only");
            psi.ArgumentList.Add("--diff-filter=U");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var paths = new List<string>();
            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length > 0)
                    paths.Add(line);
            }
            return paths;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = featureWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("rebase");
        psi.ArgumentList.Add("--abort");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode == 0)
            return new GitOpResult(true, null);

        // Treat "no rebase in progress" as success (idempotent)
        var stderrTrimmed = stderr.Trim();
        if (stderrTrimmed.Contains("no rebase in progress", StringComparison.OrdinalIgnoreCase))
            return new GitOpResult(true, null);

        return new GitOpResult(false, stderrTrimmed);
    }

    public async Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = mainWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("merge");
        psi.ArgumentList.Add("--ff-only");
        psi.ArgumentList.Add(mergeRef);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode == 0)
            return new GitOpResult(true, null);

        var trimmed = stderr.Trim();
        return new GitOpResult(false, trimmed.Length > 0 ? trimmed : $"git merge --ff-only exited with exit code {proc.ExitCode} and produced no stderr output");
    }

    public async Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = mainWorktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("branch");
        psi.ArgumentList.Add(force ? "-D" : "-d");
        psi.ArgumentList.Add(branch);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode == 0)
            return new GitOpResult(true, null);

        return new GitOpResult(false, stderr.Trim());
    }

    public async Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                // "??" prefix means untracked - skip those, keep everything else
                if (!line.StartsWith("??"))
                    lines.Add(line);
            }
            return lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--get");
            psi.ArgumentList.Add($"remote.{remote}.url");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && stdout.Trim().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("rev-list");
            psi.ArgumentList.Add("--count");
            psi.ArgumentList.Add(range);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return 0;
            return int.TryParse(stdout.Trim(), out var count) ? count : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add("--oneline");
            if (limit > 0)
            {
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(limit.ToString());
            }
            psi.ArgumentList.Add(range);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                lines.Add(line);
            }
            return lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        string[] args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }
        return stdout;
    }
}
