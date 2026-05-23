namespace ThroughlineBuild.Helpers;

public static class PhaseWorktreeLayout
{
    public static PhaseWorktreeNames Compute(string ticketId, string title, string mainWorktreePath)
    {
        var slug = SlugBuilder.BuildBranchSlug(ticketId, title);
        var branch = "ticket/" + slug;
        var worktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-" + slug));
        return new PhaseWorktreeNames(slug, branch, worktree);
    }
}

public record PhaseWorktreeNames(
    string Slug,
    string BranchName,
    string WorktreePath);
