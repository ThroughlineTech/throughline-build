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

        // Assert
        Assert.Equal("tlb-42-add-implement-phase", result.Slug);
        Assert.Equal("ticket/tlb-42-add-implement-phase", result.BranchName);
        var expectedWorktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-tlb-42-add-implement-phase"));
        Assert.Equal(expectedWorktree, result.WorktreePath);
    }

    [Fact]
    public void Compute_SpecialCharacterTitle_DelegatesSlugBuildingAndNoParallelLogic()
    {
        // Arrange
        var ticketId = "TLB-10";
        var title = "Fix: auth & session!!";
        var mainWorktreePath = Path.GetTempPath();  // Use an absolute path that exists

        // Act
        var result = PhaseWorktreeLayout.Compute(ticketId, title, mainWorktreePath);

        // Assert - verifies SlugBuilder delegation (special chars collapsed to single hyphens)
        Assert.Equal("tlb-10-fix-auth-session", result.Slug);
        Assert.Equal("ticket/tlb-10-fix-auth-session", result.BranchName);
        var expectedWorktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-tlb-10-fix-auth-session"));
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
        Assert.Equal("tlb-5-test-relative-path", result.Slug);
        Assert.Equal("ticket/tlb-5-test-relative-path", result.BranchName);
    }
}
