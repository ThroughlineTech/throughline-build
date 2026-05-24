using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Git;
using Xunit;

namespace ThroughlineBuild.Git.Tests;

public class ProcessGitClientDiffTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // Creates a fresh git repo with an initial commit and returns the repo path.
    private string CreateTempGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        RunGit(dir, "init");
        RunGit(dir, "config", "user.email", "test@test.com");
        RunGit(dir, "config", "user.name", "Test");
        // Seed a file on main so we have something to diff against
        File.WriteAllText(Path.Combine(dir, "base.txt"), "base content\n");
        RunGit(dir, "add", "base.txt");
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

    // Returns the HEAD SHA without throwing.
    private static string GetHead(string repoDir)
    {
        var psi = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = repoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.StandardOutput.ReadToEnd().Trim();
    }

    [Fact]
    public async Task DiffAsync_SameRef_ReturnsEmptyEntries()
    {
        var repo = CreateTempGitRepo();
        var client = new ProcessGitClient(repo);
        var head = GetHead(repo);

        var diff = await client.DiffAsync(head, head, repo, false, CancellationToken.None);

        Assert.Equal(head, diff.FromRef);
        Assert.Equal(head, diff.ToRef);
        Assert.Empty(diff.Entries);
    }

    [Fact]
    public async Task DiffAsync_AddedModifiedDeleted_ReturnsCorrectKinds()
    {
        var repo = CreateTempGitRepo();

        // Add a file that we will later delete on the feature branch
        File.WriteAllText(Path.Combine(repo, "to_delete.txt"), "will be deleted\n");
        RunGit(repo, "add", "to_delete.txt");
        RunGit(repo, "commit", "-m", "add to_delete");
        var baseSha = GetHead(repo);

        // Create a feature branch from here
        RunGit(repo, "checkout", "-b", "add-mod-del");

        // Add a new file
        File.WriteAllText(Path.Combine(repo, "added.txt"), "new file\n");
        RunGit(repo, "add", "added.txt");

        // Modify the existing file
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base content\nmodified\n");
        RunGit(repo, "add", "base.txt");

        // Delete the file
        RunGit(repo, "rm", "to_delete.txt");

        RunGit(repo, "commit", "-m", "add, modify, delete");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, false, CancellationToken.None);

        var addedEntry = diff.Entries.FirstOrDefault(e => e.Path == "added.txt");
        var modifiedEntry = diff.Entries.FirstOrDefault(e => e.Path == "base.txt");
        var deletedEntry = diff.Entries.FirstOrDefault(e => e.Path == "to_delete.txt");

        Assert.NotNull(addedEntry);
        Assert.Equal(DiffKind.Added, addedEntry!.Kind);

        Assert.NotNull(modifiedEntry);
        Assert.Equal(DiffKind.Modified, modifiedEntry!.Kind);

        Assert.NotNull(deletedEntry);
        Assert.Equal(DiffKind.Deleted, deletedEntry!.Kind);
    }

    [Fact]
    public async Task DiffAsync_Renamed_ReturnsRenamedKindWithOldPath()
    {
        var repo = CreateTempGitRepo();

        // Add a file to rename
        File.WriteAllText(Path.Combine(repo, "old_name.txt"), "content\n");
        RunGit(repo, "add", "old_name.txt");
        RunGit(repo, "commit", "-m", "add file to rename");
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "rename-branch");
        RunGit(repo, "mv", "old_name.txt", "new_name.txt");
        RunGit(repo, "commit", "-m", "rename file");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, false, CancellationToken.None);

        Assert.Single(diff.Entries);
        var entry = diff.Entries[0];
        Assert.Equal(DiffKind.Renamed, entry.Kind);
        Assert.Equal("new_name.txt", entry.Path);
        Assert.Equal("old_name.txt", entry.OldPath);
    }

    [Fact]
    public async Task DiffAsync_PatchContentTrue_NonNullForModifiedFile()
    {
        var repo = CreateTempGitRepo();
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "patch-branch");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base content\nline2\nline3\n");
        RunGit(repo, "add", "base.txt");
        RunGit(repo, "commit", "-m", "modify base");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, true, CancellationToken.None);

        Assert.Single(diff.Entries);
        var entry = diff.Entries[0];
        Assert.Equal("base.txt", entry.Path);
        Assert.NotNull(entry.PatchContent);
        Assert.Contains("@@", entry.PatchContent);
        Assert.Contains("base content", entry.PatchContent);
    }

    [Fact]
    public async Task DiffAsync_PatchContentFalse_AllEntriesHaveNullPatch()
    {
        var repo = CreateTempGitRepo();
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "no-patch-branch");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "modified\n");
        RunGit(repo, "add", "base.txt");
        RunGit(repo, "commit", "-m", "modify");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, false, CancellationToken.None);

        Assert.All(diff.Entries, e => Assert.Null(e.PatchContent));
    }

    [Fact]
    public async Task DiffAsync_LineCounts_MatchNumstat()
    {
        var repo = CreateTempGitRepo();
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "linecount-branch");
        // Add 3 lines to base.txt (it currently has 1 line "base content\n")
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base content\nline2\nline3\nline4\n");
        RunGit(repo, "add", "base.txt");
        RunGit(repo, "commit", "-m", "add 3 lines");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, false, CancellationToken.None);

        Assert.Single(diff.Entries);
        var entry = diff.Entries[0];
        Assert.Equal(3, entry.LinesAdded);
        Assert.Equal(0, entry.LinesRemoved);
    }

    [Fact]
    public async Task DiffAsync_BinaryFile_LineCountsAreZero()
    {
        var repo = CreateTempGitRepo();
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "binary-branch");
        // Write a binary file with embedded NUL bytes
        var binaryPath = Path.Combine(repo, "data.bin");
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x00, 0xFF, 0xFE };
        File.WriteAllBytes(binaryPath, binaryData);
        RunGit(repo, "add", "data.bin");
        RunGit(repo, "commit", "-m", "add binary");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, false, CancellationToken.None);

        var binaryEntry = diff.Entries.FirstOrDefault(e => e.Path == "data.bin");
        Assert.NotNull(binaryEntry);
        Assert.Equal(0, binaryEntry!.LinesAdded);
        Assert.Equal(0, binaryEntry.LinesRemoved);
    }

    [Fact]
    public async Task DiffAsync_LargeFile_PatchContentCappedToNull()
    {
        var repo = CreateTempGitRepo();
        var baseSha = GetHead(repo);

        RunGit(repo, "checkout", "-b", "large-branch");
        // Write a file larger than 100 KB
        var sb = new StringBuilder();
        // Each line is ~50 chars; 3000 lines = ~150 KB
        for (int i = 0; i < 3000; i++)
            sb.AppendLine($"line {i:D5}: padding-padding-padding-padding-padding");
        File.WriteAllText(Path.Combine(repo, "large.txt"), sb.ToString());
        RunGit(repo, "add", "large.txt");
        RunGit(repo, "commit", "-m", "add large file");
        var featureSha = GetHead(repo);

        var client = new ProcessGitClient(repo);
        var diff = await client.DiffAsync(baseSha, featureSha, repo, true, CancellationToken.None);

        var largeEntry = diff.Entries.FirstOrDefault(e => e.Path == "large.txt");
        Assert.NotNull(largeEntry);
        // File is listed in Entries
        Assert.Contains(diff.Entries, e => e.Path == "large.txt");
        // But PatchContent is null due to size cap
        Assert.Null(largeEntry!.PatchContent);
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
