using System.Diagnostics;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Git;

public sealed class ProcessGitClient : IGitClient
{
    // Hard ceiling on any single git invocation. Local git ops complete in milliseconds;
    // this only ever fires when git wedges (e.g. the Windows MSYS-git console-handshake
    // deadlock that hung a chain's final no-op merge for 20+ minutes). Network ops get a
    // larger budget because fetch/push legitimately take longer.
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NetworkGitTimeout = TimeSpan.FromMinutes(5);

    private readonly string? _workingDirectory;

    public ProcessGitClient(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
    }

    // Captured result of one git invocation. ExitCode is -1 when the process timed out or
    // was killed before exiting; TimedOut distinguishes that from an ordinary non-zero exit.
    private readonly record struct GitRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// Runs a git subprocess safely and returns its captured output. This is the single
    /// choke point every git invocation in this class flows through; it exists to kill an
    /// entire class of hangs seen on Windows:
    ///   - Drains stdout AND stderr concurrently. Reading only one stream (or both
    ///     sequentially) deadlocks the moment git fills the other pipe's ~64 KB OS buffer
    ///     while the parent is blocked on the first - e.g. a fast-forward merge whose
    ///     diffstat overflows stdout while we wait on stderr.
    ///   - Redirects and immediately closes stdin so git can never block waiting on input.
    ///   - Sets GIT_PAGER=cat / GIT_TERMINAL_PROMPT=0 / GIT_OPTIONAL_LOCKS=0 and
    ///     CreateNoWindow so the MSYS git client does not spin up its own conhost and park
    ///     on the CSRSS console handshake.
    ///   - Enforces a hard timeout; on expiry it kills the whole process tree (git plus any
    ///     conhost it spawned) and returns TimedOut instead of hanging the caller forever.
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/>; a timeout
    /// does not - it surfaces as <c>TimedOut</c> so callers can report a clear failure.
    /// </summary>
    private static async Task<GitRun> RunGitCaptureAsync(
        ProcessStartInfo psi,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultGitTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedCt = linkedCts.Token;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        // We never write to git; close stdin so it can never block waiting on input.
        try { proc.StandardInput.Close(); } catch { /* best effort */ }

        // Start draining both pipes before awaiting exit (see method remarks).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCt);
        var stderrTask = proc.StandardError.ReadToEndAsync(linkedCt);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(linkedCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree (git + any conhost it spawned) so nothing lingers and the
            // drained reads above unblock.
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }

            if (ct.IsCancellationRequested)
            {
                // Caller cancellation, not our timeout: drain the now-unblocked readers so no
                // task leaks, then propagate the cancellation.
                try { await stdoutTask.ConfigureAwait(false); } catch { /* ignore */ }
                try { await stderrTask.ConfigureAwait(false); } catch { /* ignore */ }
                throw;
            }

            timedOut = true;
        }

        var stdout = string.Empty;
        var stderr = string.Empty;
        try { stdout = await stdoutTask.ConfigureAwait(false); } catch { /* killed / cancelled */ }
        try { stderr = await stderrTask.ConfigureAwait(false); } catch { /* killed / cancelled */ }

        var exitCode = timedOut ? -1 : SafeExitCode(proc);
        return new GitRun(exitCode, stdout, stderr, timedOut);
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch { return -1; }
    }

    public async Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse {refspec}")
        {
            WorkingDirectory = workingDirectory
        };
        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse {refspec} failed (exit {run.ExitCode}): {run.Stderr.Trim()}");
        return run.Stdout.Trim();
    }

    public async Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
    {
        var wd = _workingDirectory ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo("git") { WorkingDirectory = wd };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--porcelain");

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"git worktree list failed (exit {run.ExitCode}): {run.Stderr.Trim()}");
        return ParseWorktreeList(run.Stdout);
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
            var psi = new ProcessStartInfo("git") { WorkingDirectory = wd };
            psi.ArgumentList.Add("branch");
            psi.ArgumentList.Add("--list");
            psi.ArgumentList.Add(pattern);
            psi.ArgumentList.Add("--no-merged");
            psi.ArgumentList.Add(baseBranch);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var result = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
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
        var psi = new ProcessStartInfo("git") { WorkingDirectory = wd };
        psi.ArgumentList.Add("worktree");
        psi.ArgumentList.Add("remove");
        if (force)
            psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add(path);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return new WorktreeRemoveResult(false, FailureDetail(run, "git worktree remove"));
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
            var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(newBranch);
            psi.ArgumentList.Add(worktreePath);
            psi.ArgumentList.Add(fromRef);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return new WorktreeCreateResult(false, FailureDetail(run, "git worktree add"), null);
            return new WorktreeCreateResult(true, null, Path.GetFullPath(worktreePath));
        }
        catch (Exception ex)
        {
            return new WorktreeCreateResult(false, ex.Message, null);
        }
    }

    public async Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = worktreePath };
            psi.ArgumentList.Add("checkout");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(branch);
            psi.ArgumentList.Add(fromRef);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return new GitOpResult(false, FailureDetail(run, "git checkout -b"));
            return new GitOpResult(true, null);
        }
        catch (Exception ex)
        {
            return new GitOpResult(false, ex.Message);
        }
    }

    public async Task<WorktreeCreateResult> CheckoutWorktreeAsync(string worktreePath, string existingBranch, string mainWorktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add(worktreePath);
            psi.ArgumentList.Add(existingBranch);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return new WorktreeCreateResult(false, FailureDetail(run, "git worktree add"), null);
            return new WorktreeCreateResult(true, null, Path.GetFullPath(worktreePath));
        }
        catch (Exception ex)
        {
            return new WorktreeCreateResult(false, ex.Message, null);
        }
    }

    public async Task<WorktreeCreateResult> CreateDetachedWorktreeAsync(string worktreePath, string sha, string mainWorktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("--detach");
            psi.ArgumentList.Add(worktreePath);
            psi.ArgumentList.Add(sha);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return new WorktreeCreateResult(false, FailureDetail(run, "git worktree add --detach"), null);
            return new WorktreeCreateResult(true, null, Path.GetFullPath(worktreePath));
        }
        catch (Exception ex)
        {
            return new WorktreeCreateResult(false, ex.Message, null);
        }
    }

    public async Task<IReadOnlyList<string>> ListLocalBranchesAsync(string pattern, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("branch");
            psi.ArgumentList.Add("--list");
            psi.ArgumentList.Add(pattern);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();
            return run.Stdout.Split('\n')
                .Select(l => l.Trim().TrimStart('*').Trim())
                .Where(l => l.Length > 0)
                .ToList()
                .AsReadOnly();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--abbrev-ref");
            psi.ArgumentList.Add("HEAD");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return "main";
            var branch = run.Stdout.Trim();
            return string.IsNullOrEmpty(branch) ? "main" : branch;
        }
        catch
        {
            return "main";
        }
    }

    // Returns the HEAD SHA of the given worktree, or empty string on failure.
    // Does not throw on git-level failure; callers check string.Length == 40 to detect failure.
    public async Task<string> HeadShaAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = worktreePath };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return string.Empty;
            return run.Stdout.Trim();
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
        var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add($"{fromRef}...{toRef}");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(newPath);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

        const int PatchSizeCap = 102400;
        if (run.Stdout.Length > PatchSizeCap)
            return null;

        return run.Stdout.Length == 0 ? null : run.Stdout;
    }

    public async Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
        psi.ArgumentList.Add("fetch");
        psi.ArgumentList.Add(remote);

        var run = await RunGitCaptureAsync(psi, ct, NetworkGitTimeout).ConfigureAwait(false);
        var stderrTrimmed = run.Stderr.Trim();
        if (run.ExitCode != 0)
            return new GitOpResult(false, FailureDetail(run, "git fetch", NetworkGitTimeout));
        return new GitOpResult(true, null, stderrTrimmed.Length > 0 ? stderrTrimmed : null);
    }

    public async Task<GitOpResult> PushAsync(string remote, string branch, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
        psi.ArgumentList.Add("push");
        psi.ArgumentList.Add(remote);
        psi.ArgumentList.Add(branch);

        var run = await RunGitCaptureAsync(psi, ct, NetworkGitTimeout).ConfigureAwait(false);
        var stderrTrimmed = run.Stderr.Trim();
        if (run.ExitCode != 0)
            return new GitOpResult(false, FailureDetail(run, "git push", NetworkGitTimeout));
        return new GitOpResult(true, null, stderrTrimmed.Length > 0 ? stderrTrimmed : null);
    }

    public async Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = featureWorktreePath };
        psi.ArgumentList.Add("rebase");
        psi.ArgumentList.Add(ontoRef);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

        if (run.ExitCode == 0)
            return new RebaseResult(true, false, Array.Empty<string>(), null);

        // Non-zero: check for unmerged paths (conflicts)
        var conflictingPaths = await GetUnmergedPathsAsync(featureWorktreePath, ct).ConfigureAwait(false);
        bool hadConflicts = conflictingPaths.Count > 0;
        return new RebaseResult(false, hadConflicts, conflictingPaths, FailureDetail(run, "git rebase"));
    }

    private static async Task<IReadOnlyList<string>> GetUnmergedPathsAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--name-only");
            psi.ArgumentList.Add("--diff-filter=U");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

            var paths = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
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
        var psi = new ProcessStartInfo("git") { WorkingDirectory = featureWorktreePath };
        psi.ArgumentList.Add("rebase");
        psi.ArgumentList.Add("--abort");

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

        if (run.ExitCode == 0)
            return new GitOpResult(true, null);

        // Treat "no rebase in progress" as success (idempotent)
        var stderrTrimmed = run.Stderr.Trim();
        if (stderrTrimmed.Contains("no rebase in progress", StringComparison.OrdinalIgnoreCase))
            return new GitOpResult(true, null);

        return new GitOpResult(false, FailureDetail(run, "git rebase --abort"));
    }

    public async Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct)
    {
        // No-op fast path: if mergeRef is already contained in HEAD the merge is a guaranteed
        // "Already up to date." Skip spawning git merge entirely - that no-op merge is exactly
        // the call that wedged a completed chain for 20+ minutes on the Windows MSYS-git console
        // handshake. merge-base --is-ancestor is cheap, mutates nothing, and on its own failure
        // returns false so we simply fall through to the real merge.
        if (await IsAncestorAsync(mergeRef, "HEAD", mainWorktreePath, ct).ConfigureAwait(false))
            return new GitOpResult(true, null);

        var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
        psi.ArgumentList.Add("merge");
        psi.ArgumentList.Add("--ff-only");
        psi.ArgumentList.Add(mergeRef);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
        var trimmed = run.Stderr.Trim();

        if (run.ExitCode == 0)
            return new GitOpResult(true, null, trimmed.Length > 0 ? trimmed : null);

        if (run.TimedOut)
            return new GitOpResult(false, $"git merge --ff-only {mergeRef} timed out after {DefaultGitTimeout.TotalSeconds:0}s and was killed");

        return new GitOpResult(false, trimmed.Length > 0 ? trimmed : $"git merge --ff-only exited with exit code {run.ExitCode} and produced no stderr output");
    }

    public async Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
        psi.ArgumentList.Add("branch");
        psi.ArgumentList.Add(force ? "-D" : "-d");
        psi.ArgumentList.Add(branch);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

        if (run.ExitCode == 0)
            return new GitOpResult(true, null);

        return new GitOpResult(false, FailureDetail(run, "git branch -d"));
    }

    public async Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
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

    public async Task<IReadOnlyList<string>> GetConflictedPathsAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var paths = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length < 4) continue;
                // porcelain format: XY<space>path
                // Conflict codes involve U (unmerged) in either column, or DD/AA
                var x = line[0];
                var y = line[1];
                if (IsConflictCode(x, y))
                    paths.Add(line.Substring(3));
            }
            return paths;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // Conflict codes per git status --porcelain spec:
    //   DD (both deleted), AU, UD, UA, DU, AA (both added), UU (both modified)
    private static bool IsConflictCode(char x, char y) =>
        (x == 'U' || y == 'U') ||
        (x == 'A' && y == 'A') ||
        (x == 'D' && y == 'D');

    public async Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("stash");
            psi.ArgumentList.Add("list");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var entries = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length > 0)
                    entries.Add(line);
            }
            return entries;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<GitOpResult> StashDropAsync(string stashRef, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("stash");
            psi.ArgumentList.Add("drop");
            psi.ArgumentList.Add(stashRef);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);

            return run.ExitCode == 0
                ? new GitOpResult(true, null)
                : new GitOpResult(false, FailureDetail(run, "git stash drop"));
        }
        catch (Exception ex)
        {
            return new GitOpResult(false, ex.Message);
        }
    }

    public async Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--get");
            psi.ArgumentList.Add($"remote.{remote}.url");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            return run.ExitCode == 0 && run.Stdout.Trim().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RemoteBranchExistsAsync(string remote, string branch, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--verify");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add($"refs/remotes/{remote}/{branch}");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            return run.ExitCode == 0;
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
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("rev-list");
            psi.ArgumentList.Add("--count");
            psi.ArgumentList.Add(range);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return 0;
            return int.TryParse(run.Stdout.Trim(), out var count) ? count : 0;
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
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add("--oneline");
            if (limit > 0)
            {
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(limit.ToString());
            }
            psi.ArgumentList.Add(range);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var raw in run.Stdout.Split('\n'))
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

    public async Task<IReadOnlyList<string>> LogShasAsync(string range, int limit, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add("--pretty=format:%H");
            if (limit > 0)
            {
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(limit.ToString());
            }
            psi.ArgumentList.Add(range);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var raw in run.Stdout.Split('\n'))
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

    public async Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("merge-base");
            psi.ArgumentList.Add("--is-ancestor");
            psi.ArgumentList.Add(ancestor);
            psi.ArgumentList.Add(descendant);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            return run.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--stat");
            psi.ArgumentList.Add("--name-only");
            psi.ArgumentList.Add(range);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var files = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                files.Add(line);
            }
            return files;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<DivergenceState> ProbeDivergenceAsync(string mainWorktreePath, string baseBranch, string remote, CancellationToken ct)
    {
        try
        {
            var localRef = baseBranch;
            var remoteRef = $"{remote}/{baseBranch}";

            var localIsAncestorOfRemote = await IsAncestorAsync(localRef, remoteRef, mainWorktreePath, ct).ConfigureAwait(false);
            var remoteIsAncestorOfLocal = await IsAncestorAsync(remoteRef, localRef, mainWorktreePath, ct).ConfigureAwait(false);

            if (localIsAncestorOfRemote && remoteIsAncestorOfLocal)
                return DivergenceState.Clean;
            if (localIsAncestorOfRemote)
                return DivergenceState.RemoteAhead;
            if (remoteIsAncestorOfLocal)
                return DivergenceState.LocalAhead;

            // Diverged - use merge-tree to check for conflicts without mutating anything.
            var psi = new ProcessStartInfo("git") { WorkingDirectory = mainWorktreePath };
            psi.ArgumentList.Add("merge-tree");
            psi.ArgumentList.Add("--write-tree");
            psi.ArgumentList.Add(localRef);
            psi.ArgumentList.Add(remoteRef);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            return run.ExitCode == 0 ? DivergenceState.DivergedNoConflict : DivergenceState.DivergedWithConflict;
        }
        catch
        {
            return DivergenceState.DivergedWithConflict;
        }
    }

    public async Task<IReadOnlyList<string>> GetUntrackedFilesAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add("--others");
            psi.ArgumentList.Add("--exclude-standard");

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length > 0)
                    lines.Add(line);
            }
            return lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> FilterTrackedPathsAsync(IReadOnlyList<string> paths, string workingDirectory, CancellationToken ct)
    {
        if (paths.Count == 0)
            return Array.Empty<string>();

        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add("--");
            foreach (var path in paths)
                psi.ArgumentList.Add(path);

            var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return Array.Empty<string>();

            var lines = new List<string>();
            foreach (var rawLine in run.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length > 0)
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
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var run = await RunGitCaptureAsync(psi, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            var suffix = run.TimedOut ? " - timed out and was killed" : "";
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed (exit {run.ExitCode}){suffix}: {run.Stderr.Trim()}");
        }
        return run.Stdout;
    }

    // Builds a failure message for a git op, surfacing git's own stderr verbatim and falling
    // back to a clear timeout / exit-code note when stderr is empty.
    private static string FailureDetail(GitRun run, string command, TimeSpan? timeout = null)
    {
        var trimmed = run.Stderr.Trim();
        if (trimmed.Length > 0)
            return trimmed;
        if (run.TimedOut)
            return $"{command} timed out after {(timeout ?? DefaultGitTimeout).TotalSeconds:0}s and was killed";
        return $"{command} exited with exit code {run.ExitCode} and produced no stderr output";
    }
}
