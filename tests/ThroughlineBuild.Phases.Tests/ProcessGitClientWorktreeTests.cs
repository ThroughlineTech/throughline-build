using System.Diagnostics;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ProcessGitClientWorktreeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

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
