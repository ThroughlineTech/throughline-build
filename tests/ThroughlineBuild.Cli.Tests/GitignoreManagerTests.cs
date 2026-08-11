using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for the append-only .gitignore merge: never clobbers existing content, adds only the
/// missing standard entries, and is idempotent.
/// </summary>
public class GitignoreManagerTests
{
    [Fact]
    public void Merge_NullFile_WritesHeaderAndAllEntries()
    {
        var result = GitignoreManager.Merge(null);

        Assert.NotNull(result);
        Assert.Contains(GitignoreManager.ManagedHeader, result);
        Assert.All(GitignoreManager.RequiredEntries, e => Assert.Contains(e, result));
    }

    [Fact]
    public void Merge_FullyCovered_ReturnsNull()
    {
        var existing = string.Join("\n", GitignoreManager.RequiredEntries) + "\n";
        Assert.Null(GitignoreManager.Merge(existing));
    }

    [Fact]
    public void Merge_PreservesExistingContentVerbatim()
    {
        var existing = "node_modules/\n# my notes\ndist/\n";
        var result = GitignoreManager.Merge(existing)!;

        Assert.StartsWith(existing, result.Replace("\r\n", "\n"));
        Assert.Contains("node_modules/", result);
        Assert.Contains("# my notes", result);
        Assert.Contains(".worktrees/", result); // appended
    }

    [Fact]
    public void Merge_AppendsOnlyMissingEntries()
    {
        var existing = ".build/*.md\n.worktrees/\n";
        var missing = GitignoreManager.MissingEntries(existing);

        Assert.DoesNotContain(".build/*.md", missing);
        Assert.DoesNotContain(".worktrees/", missing);
        Assert.Contains("secrets/", missing);
    }

    [Fact]
    public void RequiredEntries_ContainsBuildMdWildcard()
    {
        // The wildcard .build/*.md covers tool-generated markdown files (brief, ticket stubs, etc.)
        // and is the fix for the recurring untracked-file collision at ship (TLB-489).
        Assert.Contains(".build/*.md", GitignoreManager.RequiredEntries);
        // The narrower .build/brief.md entry is superseded by the wildcard.
        Assert.DoesNotContain(".build/brief.md", GitignoreManager.RequiredEntries);
    }

    [Fact]
    public void RequiredEntries_IgnoreInstallHandoffsAndMachineLocalSopState()
    {
        Assert.Contains(".build/profile.json", GitignoreManager.RequiredEntries);
        Assert.Contains(".build/invariants.toml", GitignoreManager.RequiredEntries);
        Assert.Contains(".build/conductor.toml", GitignoreManager.RequiredEntries);
        Assert.Contains(".build/sop-manifest.json", GitignoreManager.RequiredEntries);
    }

    [Fact]
    public void RequiredEntries_DoesNotIgnoreConfigToml()
    {
        // config.toml is tracked (TLB-627): it carries repository facts ([[review.checks]], [waves],
        // [worktree], etc.) that must travel with a clone, so it must never be in the ignore list.
        Assert.DoesNotContain(".build/config.toml", GitignoreManager.RequiredEntries);
    }

    [Fact]
    public void Merge_IsIdempotent_SecondPassReturnsNull()
    {
        var first = GitignoreManager.Merge("node_modules/\n")!;
        var second = GitignoreManager.Merge(first);

        Assert.Null(second); // everything required is now present
    }

    [Fact]
    public void Merge_DoesNotDuplicateHeaderOnRerun()
    {
        // First pass adds header + entries; introduce a brand-new fictional miss is not possible,
        // so re-running with the produced file must return null (no second header).
        var produced = GitignoreManager.Merge(null)!;
        Assert.Single(Occurrences(produced, GitignoreManager.ManagedHeader));
        Assert.Null(GitignoreManager.Merge(produced));
    }

    [Fact]
    public void MissingEntries_IgnoresCommentsAndBlankLines()
    {
        // A required entry that appears only inside a comment is still "missing".
        var existing = "# .worktrees/ is intentionally not ignored here\n\n";
        Assert.Contains(".worktrees/", GitignoreManager.MissingEntries(existing));
    }

    private static IEnumerable<int> Occurrences(string haystack, string needle)
    {
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            yield return idx;
            idx += needle.Length;
        }
    }
}
