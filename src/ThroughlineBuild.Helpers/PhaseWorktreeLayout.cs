using System.Text.RegularExpressions;

namespace ThroughlineBuild.Helpers;

public static class PhaseWorktreeLayout
{
    public static PhaseWorktreeNames Compute(string ticketId, string title, string mainWorktreePath)
    {
        // Branch and worktree names use the ticket id only (no title slug). This keeps the
        // worktree directory path short so deep repo trees stay under the Windows MAX_PATH
        // limit; the title slug previously pushed long paths past it and broke worktree create.
        var slug = SlugBuilder.BuildTicketSlug(ticketId);
        var branch = "ticket/" + slug;
        var worktree = Path.GetFullPath(Path.Combine(mainWorktreePath, ".worktrees", "ticket-" + slug));
        return new PhaseWorktreeNames(slug, branch, worktree);
    }

    /// <summary>
    /// The canonical branch name for a ticket: "ticket/{id}".
    /// </summary>
    public static string BranchName(string ticketId) => "ticket/" + SlugBuilder.BuildTicketSlug(ticketId);

    /// <summary>
    /// True when <paramref name="branchName"/> is the ticket's branch. Matches the canonical
    /// "ticket/{id}" exactly, and also matches the legacy "ticket/{id}-{slug}" form so in-flight
    /// worktrees created before this naming change still resolve. The hyphen boundary keeps
    /// "ticket/24" from matching "ticket/240".
    /// </summary>
    public static bool IsTicketBranch(string branchName, string canonicalBranchName)
    {
        if (string.IsNullOrEmpty(branchName))
            return false;
        return string.Equals(branchName, canonicalBranchName, StringComparison.OrdinalIgnoreCase)
            || branchName.StartsWith(canonicalBranchName + "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="text"/> mentions the ticket branch as a whole token - used to
    /// decide whether a "git stash list" line belongs to this ticket. The branch must not be
    /// immediately followed by an alphanumeric, so "ticket/24" does not match "ticket/240" while
    /// both the canonical ("...ticket/24:") and legacy ("...ticket/24-slug:") forms do.
    /// </summary>
    public static bool MentionsBranch(string text, string canonicalBranchName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(canonicalBranchName))
            return false;
        return Regex.IsMatch(text, Regex.Escape(canonicalBranchName) + "(?![a-zA-Z0-9])", RegexOptions.IgnoreCase);
    }
}

public record PhaseWorktreeNames(
    string Slug,
    string BranchName,
    string WorktreePath);
