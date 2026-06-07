using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Detects dirty, conflicted, or stash-polluted working-tree state and returns an
/// attributed stop message so phase entry can fail fast instead of discovering the
/// problem opaquely at ship.
/// </summary>
internal static class WorkingTreeHygieneGate
{
    /// <summary>
    /// Checks the working tree at <paramref name="worktreePath"/> for:
    /// - unmerged / conflicted paths (UU, AA, DD, AU, UA, UD, DU codes)
    /// - dangling stash entries unrelated to <paramref name="ticketBranchName"/>
    ///
    /// Returns null when the tree is clean and the gate passes.
    /// Returns a non-null stop message when the gate should block.
    ///
    /// Does NOT detect ordinary uncommitted modifications; callers that already
    /// check GetTrackedChangesAsync handle those separately.
    /// </summary>
    internal static async Task<string?> CheckAsync(
        IGitClient git,
        string worktreePath,
        string ticketBranchName,
        CancellationToken ct)
    {
        var parts = new List<string>();

        // Check for conflicted/unmerged paths
        var conflicted = await git.GetConflictedPathsAsync(worktreePath, ct).ConfigureAwait(false);
        if (conflicted.Count > 0)
        {
            var fileList = string.Join(", ", conflicted);
            parts.Add($"unmerged/conflicted paths: {fileList}");
        }

        // Check for stash entries that do not belong to this ticket
        var stashEntries = await git.ListStashEntriesAsync(worktreePath, ct).ConfigureAwait(false);
        var unrelatedStashes = new List<string>();
        foreach (var entry in stashEntries)
        {
            // An entry is "related" when it mentions the current ticket branch.
            // git stash list lines look like:
            //   stash@{0}: On ticket/tlb-1: some message
            //   stash@{1}: WIP on main: abc1234 commit message
            if (!PhaseWorktreeLayout.MentionsBranch(entry, ticketBranchName))
                unrelatedStashes.Add(entry);
        }
        if (unrelatedStashes.Count > 0)
        {
            var stashList = string.Join("; ", unrelatedStashes);
            parts.Add($"dangling stash from unrelated ticket/branch: {stashList}");
        }

        if (parts.Count == 0)
            return null;

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Returns the list of tracked files with uncommitted changes in <paramref name="worktreePath"/>.
    /// An empty list means the worktree is clean.  Delegates directly to
    /// <see cref="IGitClient.GetTrackedChangesAsync"/> so the contract is consistent with the
    /// interface default (returns empty on git failure, never throws).
    /// </summary>
    internal static Task<IReadOnlyList<string>> DirtyFilesCheckAsync(
        IGitClient git,
        string worktreePath,
        CancellationToken ct)
        => git.GetTrackedChangesAsync(worktreePath, ct);

    /// <summary>
    /// Builds a precise pre-flight message for <see cref="ShipPhase"/> that replaces the
    /// generic "N modified tracked files" message.  Returns null when clean.
    ///
    /// Checks both the feature worktree and the main worktree for conflicts and unrelated
    /// stashes, and also preserves the existing dirty-file detection from GetTrackedChangesAsync.
    /// </summary>
    internal static async Task<string?> ShipPreflightAsync(
        IGitClient git,
        string featureWorktreePath,
        string mainWorktreePath,
        string ticketBranchName,
        CancellationToken ct)
    {
        var parts = new List<string>();

        // Conflicted paths in the feature worktree
        var featureConflicted = await git.GetConflictedPathsAsync(featureWorktreePath, ct).ConfigureAwait(false);
        if (featureConflicted.Count > 0)
        {
            var fileList = string.Join(", ", featureConflicted);
            parts.Add($"conflict in {featureWorktreePath}: {fileList}");
        }

        // Conflicted paths in the main worktree
        var mainConflicted = await git.GetConflictedPathsAsync(mainWorktreePath, ct).ConfigureAwait(false);
        if (mainConflicted.Count > 0)
        {
            var fileList = string.Join(", ", mainConflicted);
            parts.Add($"conflict in {mainWorktreePath}: {fileList}");
        }

        // Stash entries unrelated to this ticket (check main worktree; stash is repo-global)
        var stashEntries = await git.ListStashEntriesAsync(mainWorktreePath, ct).ConfigureAwait(false);
        var unrelatedStashes = new List<string>();
        foreach (var entry in stashEntries)
        {
            if (!PhaseWorktreeLayout.MentionsBranch(entry, ticketBranchName))
                unrelatedStashes.Add(entry);
        }
        if (unrelatedStashes.Count > 0)
        {
            var stashList = string.Join("; ", unrelatedStashes);
            parts.Add($"stash from unrelated ticket/branch: {stashList}");
        }

        // Check for untracked files in the feature worktree that are tracked in main.
        // When git rebases the feature branch onto main, it would try to create those
        // files in the worktree and fail with "would be overwritten by merge/rebase".
        var featureUntracked = await git.GetUntrackedFilesAsync(featureWorktreePath, ct).ConfigureAwait(false);
        if (featureUntracked.Count > 0)
        {
            var featureColliders = await git.FilterTrackedPathsAsync(featureUntracked, mainWorktreePath, ct).ConfigureAwait(false);
            if (featureColliders.Count > 0)
            {
                var fileList = string.Join(", ", featureColliders);
                parts.Add($"untracked file(s) in feature worktree would be overwritten by rebase: {fileList}; " +
                          "move or remove them, or add to .gitignore if they are build artifacts");
            }
        }

        // Check for untracked files in the main worktree that are tracked in the feature branch.
        // When git fast-forward merges the feature branch into main, it would try to create those
        // files in the main worktree and fail with "would be overwritten by merge".
        var mainUntracked = await git.GetUntrackedFilesAsync(mainWorktreePath, ct).ConfigureAwait(false);
        if (mainUntracked.Count > 0)
        {
            var mainColliders = await git.FilterTrackedPathsAsync(mainUntracked, featureWorktreePath, ct).ConfigureAwait(false);
            if (mainColliders.Count > 0)
            {
                var fileList = string.Join(", ", mainColliders);
                parts.Add($"untracked file(s) in main worktree would be overwritten by merge: {fileList}; " +
                          "move or remove them, or add to .gitignore if they are build artifacts");
            }
        }

        if (parts.Count == 0)
            return null;

        return string.Join(" | ", parts);
    }
}
