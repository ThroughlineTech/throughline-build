using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class ProcessGitClientDivergenceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

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

    private (string local, string upstream) CreateFixture()
    {
        var upstream = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var local = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _tempDirs.Add(upstream);
        _tempDirs.Add(local);

        Directory.CreateDirectory(upstream);
        RunGit(upstream, "init", "-b", "main");
        RunGit(upstream, "config", "user.email", "test@test.com");
        RunGit(upstream, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(upstream, "shared.txt"), "line1\nline2\n");
        File.WriteAllText(Path.Combine(upstream, "fileA.txt"), "a\n");
        File.WriteAllText(Path.Combine(upstream, "fileB.txt"), "b\n");
        RunGit(upstream, "add", ".");
        RunGit(upstream, "commit", "-m", "initial");

        RunGit(Path.GetTempPath(), "clone", upstream, local);
        RunGit(local, "config", "user.email", "test@test.com");
        RunGit(local, "config", "user.name", "Test");

        return (local, upstream);
    }

    [Fact]
    public async Task ProbeDivergenceAsync_SameCommit_ReturnsClean()
    {
        var (local, _) = CreateFixture();
        var client = new ProcessGitClient(local);

        var result = await client.ProbeDivergenceAsync(local, "main", "origin", CancellationToken.None);

        Assert.Equal(DivergenceState.Clean, result);
    }

    [Fact]
    public async Task ProbeDivergenceAsync_RemoteAhead_ReturnsRemoteAhead()
    {
        var (local, upstream) = CreateFixture();
        RunGit(upstream, "commit", "--allow-empty", "-m", "upstream-only");
        RunGit(local, "fetch", "origin");
        var client = new ProcessGitClient(local);

        var result = await client.ProbeDivergenceAsync(local, "main", "origin", CancellationToken.None);

        Assert.Equal(DivergenceState.RemoteAhead, result);
    }

    [Fact]
    public async Task ProbeDivergenceAsync_LocalAhead_ReturnsLocalAhead()
    {
        var (local, _) = CreateFixture();
        RunGit(local, "commit", "--allow-empty", "-m", "local-only");
        var client = new ProcessGitClient(local);

        var result = await client.ProbeDivergenceAsync(local, "main", "origin", CancellationToken.None);

        Assert.Equal(DivergenceState.LocalAhead, result);
    }

    [Fact]
    public async Task ProbeDivergenceAsync_DivergedNoConflict_ReturnsDivergedNoConflict()
    {
        var (local, upstream) = CreateFixture();
        File.WriteAllText(Path.Combine(upstream, "fileA.txt"), "upstream\n");
        RunGit(upstream, "add", "fileA.txt");
        RunGit(upstream, "commit", "-m", "upstream-edit-fileA");
        RunGit(local, "fetch", "origin");
        File.WriteAllText(Path.Combine(local, "fileB.txt"), "local\n");
        RunGit(local, "add", "fileB.txt");
        RunGit(local, "commit", "-m", "local-edit-fileB");
        var client = new ProcessGitClient(local);

        var result = await client.ProbeDivergenceAsync(local, "main", "origin", CancellationToken.None);

        Assert.Equal(DivergenceState.DivergedNoConflict, result);
    }

    [Fact]
    public async Task ProbeDivergenceAsync_DivergedWithConflict_ReturnsDivergedWithConflict()
    {
        var (local, upstream) = CreateFixture();
        File.WriteAllText(Path.Combine(upstream, "shared.txt"), "upstream\nline2\n");
        RunGit(upstream, "add", "shared.txt");
        RunGit(upstream, "commit", "-m", "upstream-edit-shared");
        RunGit(local, "fetch", "origin");
        File.WriteAllText(Path.Combine(local, "shared.txt"), "local\nline2\n");
        RunGit(local, "add", "shared.txt");
        RunGit(local, "commit", "-m", "local-edit-shared");
        var client = new ProcessGitClient(local);

        var result = await client.ProbeDivergenceAsync(local, "main", "origin", CancellationToken.None);

        Assert.Equal(DivergenceState.DivergedWithConflict, result);
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
