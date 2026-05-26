using System.Diagnostics;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class ProcessGitClientRemoteTests : IDisposable
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
    public async Task RemoteExistsAsync_NoRemoteConfigured_ReturnsFalse()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        var result = await client.RemoteExistsAsync("origin", repoDir, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task RemoteExistsAsync_RemoteConfigured_ReturnsTrue()
    {
        var repoDir = CreateTempGitRepo();
        RunGit(repoDir, "remote", "add", "origin", "https://example.com/x.git");
        var client = new ProcessGitClient(repoDir);

        var result = await client.RemoteExistsAsync("origin", repoDir, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task RemoteExistsAsync_WrongRemoteName_ReturnsFalse()
    {
        var repoDir = CreateTempGitRepo();
        RunGit(repoDir, "remote", "add", "origin", "https://example.com/x.git");
        var client = new ProcessGitClient(repoDir);

        var result = await client.RemoteExistsAsync("upstream", repoDir, CancellationToken.None);

        Assert.False(result);
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
