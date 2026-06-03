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

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

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

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

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
            await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_TargetBranchEqualsBaseBranch_ProducesOriginMainRef()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var localSha = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/main", localSha);
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

        Assert.Equal("origin/main", refName);
        Assert.Equal(localSha, sha);
    }

    [Fact]
    public async Task ResolveAsync_WithFeatureBranch_ProducesOriginFeatureRef()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var localSha = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/feature/payment", localSha);
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "feature/payment", CancellationToken.None);

        Assert.Equal("origin/feature/payment", refName);
        Assert.Equal(localSha, sha);
    }

    [Fact]
    public async Task ResolveAsync_WithSlashContainingBranch_HandlesSlashesCorrectly()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var localSha = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/feature/sub/thing", localSha);
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "feature/sub/thing", CancellationToken.None);

        Assert.Equal("origin/feature/sub/thing", refName);
        Assert.Equal(localSha, sha);
    }

    [Fact]
    public async Task ResolveAsync_LocalStrictlyAheadOfOrigin_PrefersLocal()
    {
        // The chain-accumulation case (TLB-411): origin/main is frozen while local main has
        // advanced by a locally-shipped sibling. The next ticket must branch from the local
        // tip so it stacks on its sibling instead of colliding at ship-time rebase.
        var repoDir = CreateTempGitRepoWithMain();
        var shaA = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/main", shaA);
        RunGit(repoDir, "commit", "--allow-empty", "-m", "local sibling B");
        var shaB = RunGit(repoDir, "rev-parse", "main");
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

        Assert.Equal("main", refName);
        Assert.Equal(shaB, sha);
        Assert.NotEqual(shaA, sha);
    }

    [Fact]
    public async Task ResolveAsync_OriginAheadOfLocal_PrefersOrigin()
    {
        var repoDir = CreateTempGitRepoWithMain();
        var shaA = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "commit", "--allow-empty", "-m", "origin commit B");
        var shaB = RunGit(repoDir, "rev-parse", "main");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/main", shaB);
        // Local main falls behind origin/main.
        RunGit(repoDir, "reset", "--hard", shaA);
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

        Assert.Equal("origin/main", refName);
        Assert.Equal(shaB, sha);
    }

    [Fact]
    public async Task ResolveAsync_LocalDivergedFromOrigin_PrefersOrigin()
    {
        // Diverged (neither is an ancestor of the other): keep origin as the canonical base.
        // Real divergence reconciliation is ShipPhase's job, not the implement base resolver's.
        var repoDir = CreateTempGitRepoWithMain();
        var shaA = RunGit(repoDir, "rev-parse", "main");
        // origin side: a commit C off A.
        RunGit(repoDir, "checkout", "-b", "tmp-origin", shaA);
        RunGit(repoDir, "commit", "--allow-empty", "-m", "origin side C");
        var shaC = RunGit(repoDir, "rev-parse", "HEAD");
        RunGit(repoDir, "update-ref", "refs/remotes/origin/main", shaC);
        RunGit(repoDir, "checkout", "main");
        // local side: a different commit B off A.
        RunGit(repoDir, "commit", "--allow-empty", "-m", "local side B");
        var client = new ProcessGitClient(repoDir);

        var (refName, sha) = await BaseRefResolver.ResolveAsync(client, repoDir, "main", CancellationToken.None);

        Assert.Equal("origin/main", refName);
        Assert.Equal(shaC, sha);
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
