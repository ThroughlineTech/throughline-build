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
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
        return stdout.Trim();
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

    [Fact]
    public async Task GetTrackedChangesResultAsync_CorruptIndex_ReturnsFailure()
    {
        var repoDir = CreateTempGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "file.txt"), "modified content");
        File.WriteAllText(Path.Combine(repoDir, ".git", "index"), "not a git index");
        var client = new ProcessGitClient(repoDir);

        var result = await client.GetTrackedChangesResultAsync(repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("index", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUntrackedFilesAsync_UntrackedFilePresent_ReturnsIt()
    {
        var repoDir = CreateTempGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "untracked.txt"), "untracked content");
        var client = new ProcessGitClient(repoDir);

        var untracked = await client.GetUntrackedFilesAsync(repoDir, CancellationToken.None);

        Assert.Single(untracked);
        Assert.Equal("untracked.txt", untracked[0]);
    }

    [Fact]
    public async Task GetUntrackedFilesAsync_IgnoredFile_NotReturned()
    {
        var repoDir = CreateTempGitRepo();
        // Write .gitignore to ignore "ignored.txt"
        File.WriteAllText(Path.Combine(repoDir, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(repoDir, "ignored.txt"), "should not appear");
        var client = new ProcessGitClient(repoDir);

        var untracked = await client.GetUntrackedFilesAsync(repoDir, CancellationToken.None);

        // .gitignore itself is untracked (not yet committed), ignored.txt is excluded
        Assert.DoesNotContain("ignored.txt", untracked);
    }

    [Fact]
    public async Task GetUntrackedFilesAsync_CleanRepo_ReturnsEmpty()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        var untracked = await client.GetUntrackedFilesAsync(repoDir, CancellationToken.None);

        Assert.Empty(untracked);
    }

    [Fact]
    public async Task GetUntrackedFilesResultAsync_CorruptIndex_ReturnsFailure()
    {
        var repoDir = CreateTempGitRepo();
        File.WriteAllText(Path.Combine(repoDir, ".git", "index"), "not a git index");
        var client = new ProcessGitClient(repoDir);

        var result = await client.GetUntrackedFilesResultAsync(repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("index", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsAncestorResultAsync_Ancestor_ReturnsTrue()
    {
        var repoDir = CreateTempGitRepo();
        var first = RunGitOutput(repoDir, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repoDir, "second.txt"), "second");
        RunGit(repoDir, "add", "second.txt");
        RunGit(repoDir, "commit", "-m", "second");
        var client = new ProcessGitClient(repoDir);

        var result = await client.IsAncestorResultAsync(first, "HEAD", repoDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.IsAncestor);
    }

    [Fact]
    public async Task IsAncestorResultAsync_NotAncestor_ReturnsFalseSuccess()
    {
        var repoDir = CreateTempGitRepo();
        var baseBranch = RunGitOutput(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(repoDir, "switch", "-c", "side");
        File.WriteAllText(Path.Combine(repoDir, "side.txt"), "side");
        RunGit(repoDir, "add", "side.txt");
        RunGit(repoDir, "commit", "-m", "side");
        RunGit(repoDir, "switch", baseBranch);
        File.WriteAllText(Path.Combine(repoDir, "main.txt"), "main");
        RunGit(repoDir, "add", "main.txt");
        RunGit(repoDir, "commit", "-m", "main");
        var client = new ProcessGitClient(repoDir);

        var result = await client.IsAncestorResultAsync("main", "side", repoDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.IsAncestor);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task IsAncestorResultAsync_BadRef_ReturnsFailure()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        var result = await client.IsAncestorResultAsync("refs/heads/does-not-exist", "HEAD", repoDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsAncestor);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task FilterTrackedPathsAsync_TrackedPathGiven_ReturnsIt()
    {
        var repoDir = CreateTempGitRepo();
        // "file.txt" was committed in CreateTempGitRepo, so it's tracked
        var client = new ProcessGitClient(repoDir);

        var tracked = await client.FilterTrackedPathsAsync(new[] { "file.txt" }, repoDir, CancellationToken.None);

        Assert.Single(tracked);
        Assert.Equal("file.txt", tracked[0]);
    }

    [Fact]
    public async Task FilterTrackedPathsAsync_UntrackedPathGiven_ReturnsEmpty()
    {
        var repoDir = CreateTempGitRepo();
        // "no-such-file.txt" does not exist in the index
        var client = new ProcessGitClient(repoDir);

        var tracked = await client.FilterTrackedPathsAsync(new[] { "no-such-file.txt" }, repoDir, CancellationToken.None);

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task FilterTrackedPathsAsync_EmptyInput_ReturnsEmpty()
    {
        var repoDir = CreateTempGitRepo();
        var client = new ProcessGitClient(repoDir);

        var tracked = await client.FilterTrackedPathsAsync(Array.Empty<string>(), repoDir, CancellationToken.None);

        Assert.Empty(tracked);
    }

    [Fact]
    public async Task FilterTrackedPathsAsync_MixedPaths_ReturnsOnlyTracked()
    {
        var repoDir = CreateTempGitRepo();
        // "file.txt" is tracked; "untracked.txt" is not
        File.WriteAllText(Path.Combine(repoDir, "untracked.txt"), "untracked");
        var client = new ProcessGitClient(repoDir);

        var tracked = await client.FilterTrackedPathsAsync(
            new[] { "file.txt", "untracked.txt" }, repoDir, CancellationToken.None);

        Assert.Single(tracked);
        Assert.Equal("file.txt", tracked[0]);
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
