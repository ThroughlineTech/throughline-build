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
}
