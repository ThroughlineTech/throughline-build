using System.Diagnostics;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class ProcessGitClientStatusTests : IDisposable
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
        // Create and commit an initial tracked file
        File.WriteAllText(Path.Combine(dir, "file.txt"), "initial content");
        RunGit(dir, "add", "file.txt");
        RunGit(dir, "commit", "-m", "initial");
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
    public async Task GetTrackedChangesAsync_ModifiedTrackedFile_ReturnsOneEntry()
    {
        var repoDir = CreateTempGitRepo();
        // Modify the tracked file without staging
        File.WriteAllText(Path.Combine(repoDir, "file.txt"), "modified content");
        var client = new ProcessGitClient(repoDir);

        var changes = await client.GetTrackedChangesAsync(repoDir, CancellationToken.None);

        Assert.Single(changes);
        Assert.DoesNotContain(changes, line => line.StartsWith("??"));
    }

    [Fact]
    public async Task GetTrackedChangesAsync_UntrackedFileOnly_ReturnsEmpty()
    {
        var repoDir = CreateTempGitRepo();
        // Add an untracked file only - no modifications to tracked files
        File.WriteAllText(Path.Combine(repoDir, "untracked.txt"), "new untracked file");
        var client = new ProcessGitClient(repoDir);

        var changes = await client.GetTrackedChangesAsync(repoDir, CancellationToken.None);

        Assert.Empty(changes);
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
