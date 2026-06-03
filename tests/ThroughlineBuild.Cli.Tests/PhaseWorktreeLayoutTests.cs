using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class PhaseWorktreeLayoutTests
{
    [Fact]
    public void Compute_CanonicalCase_ReturnsExpectedValues()
    {
        // Arrange
        var ticketId = "TLB-42";
        var title = "Add implement phase";
        var mainWorktreePath = Path.GetTempPath();  // Use an absolute path that exists

        // Act
        var result = PhaseWorktreeLayout.Compute(ticketId, title, mainWorktreePath);

        // Assert - branch/worktree use the ticket id only; the title is not part of the slug.
        Assert.Equal("tlb-42", result.Slug);
        Assert.Equal("ticket/tlb-42", result.BranchName);
        var expectedWorktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-tlb-42"));
        Assert.Equal(expectedWorktree, result.WorktreePath);
    }

    [Fact]
    public void Compute_TitleWithSpecialCharacters_IsIgnored_OnlyTicketIdUsed()
    {
        // Arrange
        var ticketId = "TLB-10";
        var title = "Fix: auth & session!!";
        var mainWorktreePath = Path.GetTempPath();  // Use an absolute path that exists

        // Act
        var result = PhaseWorktreeLayout.Compute(ticketId, title, mainWorktreePath);

        // Assert - the title is ignored entirely; only the sanitized ticket id forms the slug.
        Assert.Equal("tlb-10", result.Slug);
        Assert.Equal("ticket/tlb-10", result.BranchName);
        var expectedWorktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-tlb-10"));
        Assert.Equal(expectedWorktree, result.WorktreePath);
    }

    [Fact]
    public void Compute_RelativeMainWorktreePath_ResolvesToAbsolutePath()
    {
        // Arrange
        var ticketId = "TLB-5";
        var title = "Test relative path";
        var mainWorktreePath = ".";  // relative path

        // Act
        var result = PhaseWorktreeLayout.Compute(ticketId, title, mainWorktreePath);

        // Assert - Path.GetFullPath should have converted to absolute
        Assert.True(Path.IsPathRooted(result.WorktreePath), $"WorktreePath should be absolute, got: {result.WorktreePath}");
        Assert.Equal("tlb-5", result.Slug);
        Assert.Equal("ticket/tlb-5", result.BranchName);
    }

    [Fact]
    public void BranchName_IsTicketSlashIdOnly()
    {
        Assert.Equal("ticket/tlb-42", PhaseWorktreeLayout.BranchName("TLB-42"));
        Assert.Equal("ticket/240", PhaseWorktreeLayout.BranchName("240"));
    }

    [Theory]
    [InlineData("ticket/tlb-42", "ticket/tlb-42", true)]   // canonical exact match
    [InlineData("ticket/tlb-42-old-slug", "ticket/tlb-42", true)]   // legacy id-slug form still resolves
    [InlineData("ticket/tlb-420", "ticket/tlb-42", false)]   // boundary: tlb-42 must not match tlb-420
    [InlineData("ticket/240", "ticket/24", false)]   // numeric boundary: 24 must not match 240
    [InlineData("ticket/24", "ticket/24", true)]
    [InlineData("main", "ticket/tlb-42", false)]
    [InlineData("", "ticket/tlb-42", false)]
    public void IsTicketBranch_MatchesCanonicalAndLegacyButRespectsBoundary(
        string branchName, string canonical, bool expected)
    {
        Assert.Equal(expected, PhaseWorktreeLayout.IsTicketBranch(branchName, canonical));
    }

    [Theory]
    [InlineData("stash@{0}: On ticket/240: WIP", "ticket/240", true)]
    [InlineData("stash@{0}: WIP on ticket/240: abc1234 msg", "ticket/240", true)]
    [InlineData("stash@{0}: On ticket/240-old-slug: WIP", "ticket/240", true)]   // legacy stash line
    [InlineData("stash@{0}: On ticket/240: WIP", "ticket/24", false)]   // 24 must not claim 240's stash
    [InlineData("stash@{0}: WIP on main: abc1234 msg", "ticket/240", false)]
    public void MentionsBranch_RecognizesOwnStashWithBoundary(
        string stashLine, string canonical, bool expected)
    {
        Assert.Equal(expected, PhaseWorktreeLayout.MentionsBranch(stashLine, canonical));
    }
}
