using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Helpers;

/// <summary>
/// Removes a chain build's leftover git worktrees AND branches - the "ticket/" and "chain/"
/// namespaces the engine creates while running an operation. Worktrees are decrufted via
/// <see cref="WorktreeDecrufter"/>; a branch is deleted only when it is fully merged into
/// <paramref name="targetBranch"/> (its tip is an ancestor of the target), so an unshipped
/// branch is preserved and reported rather than destroyed.
///
/// Two call sites share this:
///  - the end-of-chain success sweep in ChainPhase (force: true - the chain owns every
///    ticket/chain worktree and all branches are merged by the time it runs), and
///  - the on-demand "build sweep" recovery verb, the path for a chain that was interrupted
///    or preserved-on-failure and so never reached its own sweep. There it defaults to
///    force: false - it only touches fully-merged artifacts and leaves anything that still
///    carries unmerged work for the operator to inspect.
///
/// Stack-agnostic: pure git + filesystem. Never deletes a branch that is not provably merged,
/// so it cannot lose committed work.
/// </summary>
public sealed class ChainWorktreeSweeper
{
    private readonly IGitClient _git;
    private readonly WorktreeDecrufter _decrufter;

    public ChainWorktreeSweeper(IGitClient git, WorktreeDecrufter? decrufter = null)
    {
        _git = git;
        _decrufter = decrufter ?? new WorktreeDecrufter(git);
    }

    /// <summary>The engine-managed branch namespaces a sweep is allowed to remove.</summary>
    public static bool IsChainBranch(string? branch) =>
        !string.IsNullOrEmpty(branch)
        && (branch.StartsWith("ticket/", StringComparison.Ordinal)
            || branch.StartsWith("chain/", StringComparison.Ordinal));

    /// <param name="mainWorktreePath">Repo main worktree; branch/worktree git ops run here.</param>
    /// <param name="targetBranch">A branch is deleted only when merged into this ref.</param>
    /// <param name="force">
    /// When false (recovery default), a worktree whose branch is not merged is left in place.
    /// When true (chain success sweep), every ticket/chain worktree is removed regardless - but
    /// branch deletion stays merged-gated either way, so committed work is never lost.
    /// </param>
    public async Task<SweepResult> SweepAsync(
        string mainWorktreePath,
        string targetBranch,
        bool force,
        CancellationToken ct)
    {
        var worktreesRemoved = new List<string>();
        var branchesDeleted = new List<string>();
        var halted = new List<string>();
        var keptUnmerged = new List<string>();
        var processed = new HashSet<string>(StringComparer.Ordinal);

        // 1. Worktree-backed ticket/chain branches: decruft the worktree, then delete the branch
        //    if it is merged and its worktree is gone (a branch still checked out in a stuck
        //    worktree cannot be deleted).
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        foreach (var w in worktrees)
        {
            if (!IsChainBranch(w.Branch)) continue;
            processed.Add(w.Branch);

            var merged = await _git.IsAncestorAsync(w.Branch, targetBranch, mainWorktreePath, ct).ConfigureAwait(false);
            if (!merged && !force)
            {
                // Unmerged work the operator did not ask to discard: leave both worktree and branch.
                keptUnmerged.Add(w.Branch);
                continue;
            }

            var result = await _decrufter.DecruftAsync(w.Path, mainWorktreePath, ct).ConfigureAwait(false);
            var worktreeGone = result.HaltedAt is null || result.HaltedAt == DecruftStep.WorktreeNotFound;
            if (worktreeGone)
                worktreesRemoved.Add(w.Path);
            else
                halted.Add($"{w.Path} (halted at {result.HaltedAt})");

            if (merged && worktreeGone)
            {
                var del = await _git.DeleteBranchAsync(w.Branch, force: true, mainWorktreePath, ct).ConfigureAwait(false);
                if (del.Success) branchesDeleted.Add(w.Branch);
                else keptUnmerged.Add(w.Branch);
            }
            else if (!merged)
            {
                // force removed the worktree but the branch carries unmerged commits - keep it.
                keptUnmerged.Add(w.Branch);
            }
        }

        // 2. Orphan ticket/chain branches with no worktree (e.g. an earlier partial cleanup
        //    removed the worktree but left the branch ref behind). Only delete when merged.
        foreach (var pattern in new[] { "ticket/*", "chain/*" })
        {
            var branches = await _git.ListLocalBranchesAsync(pattern, mainWorktreePath, ct).ConfigureAwait(false);
            foreach (var b in branches)
            {
                if (!IsChainBranch(b) || !processed.Add(b)) continue;
                var merged = await _git.IsAncestorAsync(b, targetBranch, mainWorktreePath, ct).ConfigureAwait(false);
                if (!merged) { keptUnmerged.Add(b); continue; }
                var del = await _git.DeleteBranchAsync(b, force: true, mainWorktreePath, ct).ConfigureAwait(false);
                if (del.Success) branchesDeleted.Add(b);
                else keptUnmerged.Add(b);
            }
        }

        return new SweepResult(worktreesRemoved, branchesDeleted, halted, keptUnmerged);
    }
}

/// <summary>Outcome of a <see cref="ChainWorktreeSweeper.SweepAsync"/> pass.</summary>
public sealed record SweepResult(
    IReadOnlyList<string> WorktreesRemoved,
    IReadOnlyList<string> BranchesDeleted,
    IReadOnlyList<string> WorktreesHalted,
    IReadOnlyList<string> BranchesKeptUnmerged)
{
    /// <summary>True when nothing was left behind: no halted worktree and no kept branch.</summary>
    public bool FullyClean => WorktreesHalted.Count == 0 && BranchesKeptUnmerged.Count == 0;
}
