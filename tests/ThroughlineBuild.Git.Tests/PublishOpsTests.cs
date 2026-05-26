using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class PublishOpsTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // Creates a bare repo to act as the remote, then clones it locally.
    // Returns (localPath, remotePath).
    private (string local, string remote) CreateSimulatedRemote()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(remoteDir);
        _tempDirs.Add(remoteDir);
        RunGit(remoteDir, "init", "--bare");

        // Create a local repo that uses the bare repo as origin
        var localDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(localDir);
        _tempDirs.Add(localDir);
        RunGit(localDir, "init");
        RunGit(localDir, "config", "user.email", "test@test.com");
        RunGit(localDir, "config", "user.name", "Test");
        RunGit(localDir, "remote", "add", "origin", remoteDir);
        // Make an initial commit so we can push
        File.WriteAllText(Path.Combine(localDir, "readme.txt"), "initial");
        RunGit(localDir, "add", "readme.txt");
        RunGit(localDir, "commit", "-m", "initial");
        RunGit(localDir, "push", "origin", "HEAD:main");
        return (localDir, remoteDir);
    }

    private string CreateTempGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        RunGit(dir, "init");
        RunGit(dir, "config", "user.email", "test@test.com");
        RunGit(dir, "config", "user.name", "Test");
        RunGit(dir, "commit", "--allow-empty", "-m", "initial");
        return dir;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {err}");
        }
    }

    private static string RunGitOutput(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return stdout.Trim();
    }

    // ---------------------------------------------------------------------------
    // FetchAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FetchAsync_RetrievesNewRefsFromRemote()
    {
        var (localDir, remoteDir) = CreateSimulatedRemote();

        // Clone a second local repo from remoteDir to act as "another contributor"
        var secondLocal = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(secondLocal);
        RunGit(Path.GetTempPath(), "clone", remoteDir, secondLocal);
        RunGit(secondLocal, "config", "user.email", "test@test.com");
        RunGit(secondLocal, "config", "user.name", "Test");

        // Push a new commit from secondLocal to origin
        File.WriteAllText(Path.Combine(secondLocal, "newfile.txt"), "new content");
        RunGit(secondLocal, "add", "newfile.txt");
        RunGit(secondLocal, "commit", "-m", "add newfile");
        RunGit(secondLocal, "push", "origin", "HEAD:main");

        // Get SHA of origin/main before fetch in localDir
        var beforeFetch = RunGitOutput(localDir, "rev-parse", "origin/main");

        var client = new ProcessGitClient(localDir);
        var result = await client.FetchAsync("origin", localDir, CancellationToken.None);

        Assert.True(result.Success, $"FetchAsync failed: {result.FailureReason}");
        Assert.Null(result.FailureReason);

        // origin/main should have advanced
        var afterFetch = RunGitOutput(localDir, "rev-parse", "origin/main");
        Assert.NotEqual(beforeFetch, afterFetch);
    }

    // ---------------------------------------------------------------------------
    // RebaseAsync - clean rebase
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebaseAsync_SucceedsCleanlyWhenFeatureIsBehindMain()
    {
        var repoDir = CreateTempGitRepo();

        // Detect main branch name before creating feature branch
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create a feature branch from the initial commit
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "feature.txt"), "feature work");
        RunGit(repoDir, "add", "feature.txt");
        RunGit(repoDir, "commit", "-m", "feature commit");

        // Advance main with a new commit (on a different file)
        RunGit(repoDir, "checkout", mainBranch);
        File.WriteAllText(Path.Combine(repoDir, "mainwork.txt"), "main advance");
        RunGit(repoDir, "add", "mainwork.txt");
        RunGit(repoDir, "commit", "-m", "main advance");

        // Switch to feature branch for rebase
        RunGit(repoDir, "checkout", "feature");

        var client = new ProcessGitClient(repoDir);
        var result = await client.RebaseAsync(mainBranch, repoDir, CancellationToken.None);

        Assert.True(result.Success, $"RebaseAsync failed: {result.FailureReason}");
        Assert.False(result.HadConflicts);
        Assert.Empty(result.ConflictingPaths);
        Assert.Null(result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // RebaseAsync - conflict detection
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebaseAsync_DetectsConflictsAndPopulatesConflictingPaths()
    {
        var repoDir = CreateTempGitRepo();

        // Detect main branch name
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Write a file on main
        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "line1\nline2\nline3");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "add shared file");

        // Create feature branch and modify shared.txt
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "FEATURE\nline2\nline3");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "feature modifies shared");

        // Back to main, modify same line differently
        RunGit(repoDir, "checkout", mainBranch);
        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "MAIN\nline2\nline3");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "main modifies shared");

        // Switch to feature and attempt rebase onto main
        RunGit(repoDir, "checkout", "feature");

        var client = new ProcessGitClient(repoDir);
        var result = await client.RebaseAsync(mainBranch, repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.HadConflicts, "Expected HadConflicts to be true");
        Assert.NotEmpty(result.ConflictingPaths);
        Assert.Contains("shared.txt", result.ConflictingPaths);
        Assert.NotNull(result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // RebaseAbortAsync - leaves worktree clean
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebaseAbortAsync_LeavesWorktreeCleanAfterConflict()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "original");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "add shared");

        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "FEATURE");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "feature change");

        RunGit(repoDir, "checkout", mainBranch);
        File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "MAIN");
        RunGit(repoDir, "add", "shared.txt");
        RunGit(repoDir, "commit", "-m", "main change");

        RunGit(repoDir, "checkout", "feature");

        var client = new ProcessGitClient(repoDir);
        // Trigger conflict
        var rebaseResult = await client.RebaseAsync(mainBranch, repoDir, CancellationToken.None);
        Assert.False(rebaseResult.Success);
        Assert.True(rebaseResult.HadConflicts);

        // Now abort
        var abortResult = await client.RebaseAbortAsync(repoDir, CancellationToken.None);
        Assert.True(abortResult.Success, $"RebaseAbortAsync failed: {abortResult.FailureReason}");

        // Verify worktree is clean - git status --short should be empty
        var status = RunGitOutput(repoDir, "status", "--short");
        Assert.Equal(string.Empty, status);

        // Should be back on feature branch
        var currentBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("feature", currentBranch);
    }

    // ---------------------------------------------------------------------------
    // RebaseAbortAsync - idempotent (no rebase in progress)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebaseAbortAsync_IsIdempotentWhenNoRebaseInProgress()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        // No rebase in progress - should succeed without error
        var result = await client.RebaseAbortAsync(repoDir, CancellationToken.None);
        Assert.True(result.Success, $"RebaseAbortAsync was not idempotent: {result.FailureReason}");
    }

    // ---------------------------------------------------------------------------
    // RebaseAsync - already applied (stacked branches scenario)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebaseAsync_AlreadyAppliedReturnsSuccess()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create feature branch with a commit
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feat");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "feat commit");

        var featureSha = RunGitOutput(repoDir, "rev-parse", "HEAD");

        // Cherry-pick the feature commit onto main (simulates what happens when a predecessor ships)
        RunGit(repoDir, "checkout", mainBranch);
        RunGit(repoDir, "cherry-pick", featureSha);

        // Now rebase feature onto main (commits are already there)
        RunGit(repoDir, "checkout", "feature");

        var client = new ProcessGitClient(repoDir);
        var result = await client.RebaseAsync(mainBranch, repoDir, CancellationToken.None);

        // git exits 0 for the already-applied case (skips the commit)
        Assert.True(result.Success, $"Expected success for already-applied rebase, got: {result.FailureReason}");
        Assert.False(result.HadConflicts);
        Assert.Empty(result.ConflictingPaths);
        Assert.Null(result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // FastForwardMergeAsync - succeeds when ancestor
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FastForwardMergeAsync_SucceedsWhenMainIsAncestorOfFeature()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create feature branch ahead of main
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feature");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "feature commit");

        var featureSha = RunGitOutput(repoDir, "rev-parse", "HEAD");

        // Go back to main (which is behind feature)
        RunGit(repoDir, "checkout", mainBranch);

        var client = new ProcessGitClient(repoDir);
        var result = await client.FastForwardMergeAsync("feature", repoDir, CancellationToken.None);

        Assert.True(result.Success, $"FastForwardMergeAsync failed: {result.FailureReason}");
        Assert.Null(result.FailureReason);

        // main should now point to the feature SHA
        var mainSha = RunGitOutput(repoDir, "rev-parse", "HEAD");
        Assert.Equal(featureSha, mainSha);
    }

    // ---------------------------------------------------------------------------
    // FastForwardMergeAsync - refuses non-FF merge
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FastForwardMergeAsync_RefusesNonFastForwardMerge()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create feature branch
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feature");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "feature commit");

        // Advance main independently (diverged history)
        RunGit(repoDir, "checkout", mainBranch);
        File.WriteAllText(Path.Combine(repoDir, "main.txt"), "main");
        RunGit(repoDir, "add", "main.txt");
        RunGit(repoDir, "commit", "-m", "main commit");

        var client = new ProcessGitClient(repoDir);
        var result = await client.FastForwardMergeAsync("feature", repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Not possible to fast-forward", result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // FastForwardMergeAsync - dirty working tree returns actual git stderr
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FastForwardMergeAsync_DirtyWorkingTree_ReturnsActualGitStderr()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create feature branch with one extra commit
        RunGit(repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "feature content");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "feature commit");

        // Switch back to main and modify the same file (dirty working tree)
        RunGit(repoDir, "checkout", mainBranch);
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "uncommitted change");

        var client = new ProcessGitClient(repoDir);
        var result = await client.FastForwardMergeAsync("feature", repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("would be overwritten", result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // DeleteBranchAsync - succeeds on merged branch
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteBranchAsync_SucceedsOnMergedBranch()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        // Create and merge a feature branch
        RunGit(repoDir, "checkout", "-b", "to-delete");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feature");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "feature commit");

        RunGit(repoDir, "checkout", mainBranch);
        RunGit(repoDir, "merge", "--ff-only", "to-delete");

        var client = new ProcessGitClient(repoDir);
        var result = await client.DeleteBranchAsync("to-delete", force: false, repoDir, CancellationToken.None);

        Assert.True(result.Success, $"DeleteBranchAsync failed: {result.FailureReason}");
        Assert.Null(result.FailureReason);
    }

    // ---------------------------------------------------------------------------
    // DeleteBranchAsync - fails on unmerged branch without force
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteBranchAsync_FailsOnUnmergedBranchWithoutForce()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        RunGit(repoDir, "checkout", "-b", "unmerged");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feature");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "unmerged commit");

        RunGit(repoDir, "checkout", mainBranch);

        var client = new ProcessGitClient(repoDir);
        var result = await client.DeleteBranchAsync("unmerged", force: false, repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.True(result.FailureReason!.Length > 0);
    }

    // ---------------------------------------------------------------------------
    // DeleteBranchAsync - succeeds with force on unmerged branch
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DeleteBranchAsync_SucceedsWithForceOnUnmergedBranch()
    {
        var repoDir = CreateTempGitRepo();
        var mainBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        RunGit(repoDir, "checkout", "-b", "unmerged-force");
        File.WriteAllText(Path.Combine(repoDir, "feat.txt"), "feature");
        RunGit(repoDir, "add", "feat.txt");
        RunGit(repoDir, "commit", "-m", "unmerged commit");

        RunGit(repoDir, "checkout", mainBranch);

        var client = new ProcessGitClient(repoDir);
        var result = await client.DeleteBranchAsync("unmerged-force", force: true, repoDir, CancellationToken.None);

        Assert.True(result.Success, $"DeleteBranchAsync with force failed: {result.FailureReason}");
        Assert.Null(result.FailureReason);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
