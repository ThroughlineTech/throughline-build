using System.Diagnostics;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class BaseRefResolverTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempGitRepoWithMain()
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

    private static string RunGit(string workingDirectory, params string[] args)
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
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {err}");
        }
        return stdout.Trim();
    }

    [Fact]
    public async Task ResolveAsync_RepoWithoutOrigin_FallsBackToLocalMain()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var expectedSha = RunGit(repoDir, "rev-parse", "main");
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, CancellationToken.None);

        Assert.Equal("main", refName);
        Assert.Equal(expectedSha, sha);
    }

    [Fact]
    public async Task ResolveAsync_RepoWithOriginMain_PrefersOriginMain()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var localSha = RunGit(repoDir, "rev-parse", "main");
        // Fabricate an origin/main ref pointing at the same commit (no network needed).
        RunGit(repoDir, "update-ref", "refs/remotes/origin/main", localSha);
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, CancellationToken.None);

        Assert.Equal("origin/main", refName);
        Assert.Equal(localSha, sha);
    }

    [Fact]
    public async Task ResolveAsync_NoOriginAndNoMain_Throws()
    {
        var repoDir = CreateTempGitRepoWithMain();
        // Rename the only branch off "main" so neither origin/main nor main resolves.
        RunGit(repoDir, "branch", "-m", "main", "trunk");
        var client = new ProcessGitClient(repoDir);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await BaseRefResolver.ResolveAsync(client, repoDir, CancellationToken.None));
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
