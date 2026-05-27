using System.Diagnostics;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class ProcessGitClientWorktreeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        RunGit(dir, "init", "-b", "main");
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

    [Fact]
    public async Task ListWorktreesAsync_ReturnsMainWorktree()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        var worktrees = await client.ListWorktreesAsync(CancellationToken.None);

        Assert.NotEmpty(worktrees);
        Assert.NotEmpty(worktrees[0].Path);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_RemovesExistingWorktree()
    {
        var repoDir = CreateTempGitRepo();
        var linkedDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(linkedDir);

        RunGit(repoDir, "worktree", "add", linkedDir, "-b", "test-branch");
        Assert.True(Directory.Exists(linkedDir));

        var client = new ProcessGitClient(repoDir);
        var result = await client.RemoveWorktreeAsync(linkedDir, force: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(linkedDir));
    }

    [Fact]
    public async Task CreateWorktreeAsync_CreatesAndListsWorktree()
    {
        var repoDir = CreateTempGitRepo();
        var worktreeDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(worktreeDir);

        var client = new ProcessGitClient(repoDir);
        var result = await client.CreateWorktreeAsync(worktreeDir, "wt-test-branch", "HEAD", repoDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.AbsolutePath);

        var worktrees = await client.ListWorktreesAsync(CancellationToken.None);
        // Compare on branch name rather than path: macOS resolves /var -> /private/var when
        // git records the worktree path, but Path.GetFullPath in .NET 8 doesn't follow that
        // firmlink, so a path comparison never matches. The branch name is the unique tag
        // we just created, so checking for it proves the worktree was created and listed.
        Assert.Contains(worktrees, w => w.Branch == "wt-test-branch");
    }

    [Fact]
    public async Task CreateWorktreeAsync_FailsCleanlyWhenPathExists()
    {
        var repoDir = CreateTempGitRepo();
        var existingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(existingDir);
        Directory.CreateDirectory(existingDir);
        // Write a file so the directory is non-empty; git refuses to add a worktree
        // into a non-empty directory (exits non-zero: "not an empty directory").
        File.WriteAllText(Path.Combine(existingDir, "dummy.txt"), "block");

        var client = new ProcessGitClient(repoDir);
        var result = await client.CreateWorktreeAsync(existingDir, "wt-conflict-branch", "HEAD", repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.True(result.FailureReason!.Length > 0);
    }

    [Fact]
    public async Task HeadShaAsync_ReturnsFortyCharSha()
    {
        var repoDir = CreateTempGitRepo();
        var worktreeDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(worktreeDir);

        var client = new ProcessGitClient(repoDir);
        await client.CreateWorktreeAsync(worktreeDir, "wt-sha-branch", "HEAD", repoDir, CancellationToken.None);

        var sha = await client.HeadShaAsync(worktreeDir, CancellationToken.None);

        Assert.Equal(40, sha.Length);
        Assert.Matches("^[0-9a-f]{40}$", sha);
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
