using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Owns the integration branch and worktree lifecycle for a chain, including nested-chain
/// accumulation and the outermost landing sequence.
/// </summary>
public sealed class ChainIntegrationBranch
{
    private readonly IGitClient _git;
    private readonly string _workingDirectory;
    private readonly string? _landingRemote;
    private readonly bool _landingPushEnabled;

    public ChainIntegrationBranch(
        IGitClient git,
        string workingDirectory,
        string? landingRemote,
        bool landingPushEnabled)
    {
        _git = git;
        _workingDirectory = workingDirectory;
        _landingRemote = landingRemote;
        _landingPushEnabled = landingPushEnabled;
    }

    public static string BranchName(Ticket ticket) => BranchNameFromId(ticket.Id);

    public static string BranchNameFromId(string ticketId) =>
        $"chain/{SlugBuilder.BuildTicketSlug(ticketId)}";

    /// <summary>
    /// Ships a reviewed batch stack into the parent integration branch. The warm batch session left
    /// the integration worktree checked out on the batch branch, so this switches it back to the
    /// integration branch and fast-forwards that branch onto the batch stack tip, then marks each
    /// batched ticket Done with a shipped_at marker.
    /// </summary>
    public async Task<string?> ShipBatchStackAsync(
        IReadOnlyList<Ticket> batchTickets,
        string batchBranch,
        string integrationBranch,
        string integrationWorktreePath,
        ITicketing ticketing,
        Func<ChainEventEmitter> eventEmitterFactory,
        CancellationToken ct)
    {
        var switchResult = await _git.SwitchBranchAsync(integrationBranch, integrationWorktreePath, ct)
            .ConfigureAwait(false);
        if (!switchResult.Success)
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(batchTickets[0].Id, "batch_ship_switch_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["batch_branch"] = batchBranch,
                ["detail"] = switchResult.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"batch implemented and reviewed but could not switch the integration worktree onto " +
                $"{integrationBranch} to ship: {switchResult.FailureReason}. The work is safe on {batchBranch}.";
        }

        var ffResult = await _git.FastForwardMergeAsync(batchBranch, integrationWorktreePath, ct)
            .ConfigureAwait(false);
        if (!ffResult.Success)
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(batchTickets[0].Id, "batch_ship_merge_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["batch_branch"] = batchBranch,
                ["detail"] = ffResult.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"batch implemented and reviewed but fast-forwarding {integrationBranch} onto " +
                $"{batchBranch} failed: {ffResult.FailureReason}. The work is safe on {batchBranch}.";
        }

        string shippedSha;
        try { shippedSha = await _git.HeadShaAsync(integrationWorktreePath, ct).ConfigureAwait(false); }
        catch { shippedSha = "(unknown)"; }

        foreach (var ticket in batchTickets)
        {
            await BatchTicketWriter.RunBatchStateWriteAsync(ticket.Id,
                () => ticketing.CreateCommentAsync(ticket.Id,
                    $"<p>[shipped_at: {shippedSha}] (batch into {integrationBranch})</p>", ct)).ConfigureAwait(false);
            await BatchTicketWriter.RunBatchStateWriteAsync(ticket.Id,
                () => ticketing.TransitionAsync(ticket.Id, TicketState.Done, ct)).ConfigureAwait(false);
            await eventEmitterFactory().EmitAsync(
                EventKind.StateTransition,
                ticket.Id,
                Phase.Chain,
                new Dictionary<string, object>
                {
                    ["from"] = "InReview",
                    ["to"] = "Done",
                    ["reason"] = "batch_ship"
                }, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Lands the outermost chain's accumulated integration branch onto the target branch in the
    /// main worktree and pushes only when both a remote and push are configured.
    /// </summary>
    public async Task<string?> LandRootIntegrationBranchAsync(
        string ticketId,
        string integrationBranch,
        string integrationWorktreePath,
        string targetBranch,
        Func<ChainEventEmitter> eventEmitterFactory,
        CancellationToken ct)
    {
        var mainBranch = await _git.CurrentBranchAsync(_workingDirectory, ct).ConfigureAwait(false);
        if (!string.Equals(mainBranch, targetBranch, StringComparison.Ordinal))
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, "chain_landing_wrong_branch", new Dictionary<string, object>
            {
                ["expected"] = targetBranch,
                ["actual"] = mainBranch,
                ["integration_branch"] = integrationBranch
            }, ct).ConfigureAwait(false);
            return $"chain accumulated onto {integrationBranch} but the main worktree is on " +
                $"'{mainBranch}', not '{targetBranch}'; could not land. The work is " +
                $"safe on {integrationBranch}; switch to {targetBranch} and merge it manually.";
        }

        var landFailure = await RebaseThenFastForwardAsync(
            ticketId, integrationBranch, integrationWorktreePath,
            targetBranch, _workingDirectory, "chain_landing", eventEmitterFactory, ct).ConfigureAwait(false);
        if (landFailure is not null)
            return landFailure;

        if (_landingPushEnabled && !string.IsNullOrEmpty(_landingRemote))
        {
            var remoteConfigured = await _git.RemoteExistsAsync(_landingRemote, _workingDirectory, ct)
                .ConfigureAwait(false);
            if (!remoteConfigured)
            {
                await eventEmitterFactory().EmitAsync(
                    EventKind.TicketWrite,
                    ticketId,
                    Phase.Chain,
                    new Dictionary<string, object>
                    {
                        ["action"] = "chain_landing_push_skipped",
                        ["reason"] = "no_remote",
                        ["remote"] = _landingRemote,
                        ["target_branch"] = targetBranch
                    }, ct).ConfigureAwait(false);
                Console.WriteLine(
                    $"[{ticketId}] chain landed {integrationBranch} onto {targetBranch} " +
                    $"locally; push skipped (no '{_landingRemote}' remote configured).");
                return null;
            }

            var pushResult = await _git.PushAsync(_landingRemote, targetBranch, _workingDirectory, ct)
                .ConfigureAwait(false);
            if (!pushResult.Success)
            {
                await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, "chain_landing_push_failed", new Dictionary<string, object>
                {
                    ["target_branch"] = targetBranch,
                    ["remote"] = _landingRemote,
                    ["detail"] = pushResult.FailureReason ?? "unknown"
                }, ct).ConfigureAwait(false);
                return $"landed {integrationBranch} onto {targetBranch} locally but push " +
                    $"to {_landingRemote} failed: {pushResult.FailureReason}. Reconcile and push " +
                    $"{targetBranch} manually.";
            }
        }

        return null;
    }

    /// <summary>
    /// Rebases a chain branch onto a target and then fast-forwards the target worktree to it.
    /// Conflicted rebases are aborted, leaving the accumulated branch intact for manual recovery.
    /// </summary>
    public async Task<string?> RebaseThenFastForwardAsync(
        string ticketId,
        string branch,
        string branchWorktreePath,
        string targetRef,
        string mergeWorktreePath,
        string failureKindPrefix,
        Func<ChainEventEmitter> eventEmitterFactory,
        CancellationToken ct)
    {
        var rebase = await _git.RebaseAsync(targetRef, branchWorktreePath, ct).ConfigureAwait(false);
        if (rebase.HadConflicts)
        {
            await _git.RebaseAbortAsync(branchWorktreePath, ct).ConfigureAwait(false);
            var paths = string.Join(", ", rebase.ConflictingPaths);
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_rebase_conflicts", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["conflicting_paths"] = rebase.ConflictingPaths
            }, ct).ConfigureAwait(false);
            return $"rebasing {branch} onto {targetRef} hit conflicts in: {paths}. The work is safe on " +
                $"{branch}; rebase it onto {targetRef} and resolve, then re-run.";
        }
        if (!rebase.Success)
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_rebase_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["detail"] = rebase.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"rebasing {branch} onto {targetRef} failed: {rebase.FailureReason}. The work is safe " +
                $"on {branch}; rebase it onto {targetRef} manually, then re-run.";
        }

        var ff = await _git.FastForwardMergeAsync(branch, mergeWorktreePath, ct).ConfigureAwait(false);
        if (!ff.Success)
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_merge_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["detail"] = ff.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"landing {branch} onto {targetRef} failed: {ff.FailureReason}. The work is safe on " +
                $"{branch}; merge it manually.";
        }

        return null;
    }

    public async Task<string> ResolveWorktreePathAsync(string branch, Ticket ticket, CancellationToken ct)
    {
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        var match = worktrees.FirstOrDefault(
            w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
        return match?.Path
            ?? PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, _workingDirectory).WorktreePath;
    }

    public async Task<WorktreeCreateResult> EnsureIntegrationWorktreeAsync(
        string branch,
        string fromRef,
        string worktreePath,
        CancellationToken ct)
    {
        var existingWorktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        var existingWorktree = existingWorktrees.FirstOrDefault(
            w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
        if (existingWorktree is not null)
            return new WorktreeCreateResult(true, null, existingWorktree.Path);

        var existingBranches = await _git.ListLocalBranchesAsync(branch, _workingDirectory, ct).ConfigureAwait(false);
        if (existingBranches.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase)))
            return await _git.CheckoutWorktreeAsync(worktreePath, branch, _workingDirectory, ct).ConfigureAwait(false);

        var createResult = await _git.CreateWorktreeAsync(
            worktreePath,
            branch,
            fromRef,
            _workingDirectory,
            ct).ConfigureAwait(false);
        if (createResult.Success)
            return createResult;

        existingBranches = await _git.ListLocalBranchesAsync(branch, _workingDirectory, ct).ConfigureAwait(false);
        if (existingBranches.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase)))
            return await _git.CheckoutWorktreeAsync(worktreePath, branch, _workingDirectory, ct).ConfigureAwait(false);

        return createResult;
    }

    /// <summary>
    /// Reconciles a retained integration branch with the current base before child dispatch.
    /// Rebases rather than resetting so commits accumulated by shipped children survive.
    /// </summary>
    public async Task<string?> RefreshIntegrationBranchAsync(
        string ticketId,
        string integrationBranch,
        string integrationWorktreePath,
        string baseRef,
        Func<ChainEventEmitter> eventEmitterFactory,
        CancellationToken ct)
    {
        string chainSha;
        string baseSha;
        try
        {
            chainSha = await _git.RevParseAsync(integrationBranch, _workingDirectory, ct).ConfigureAwait(false);
            baseSha = await _git.RevParseAsync(baseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (string.Equals(chainSha, baseSha, StringComparison.Ordinal))
            return null;

        var baseIsAncestor = await _git.IsAncestorAsync(baseSha, chainSha, _workingDirectory, ct).ConfigureAwait(false);
        if (baseIsAncestor)
            return null;

        var rebase = await _git.RebaseAsync(baseRef, integrationWorktreePath, ct).ConfigureAwait(false);
        if (rebase.HadConflicts)
        {
            await _git.RebaseAbortAsync(integrationWorktreePath, ct).ConfigureAwait(false);
            var paths = string.Join(", ", rebase.ConflictingPaths);
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, "chain_refresh_rebase_conflicts", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["base_ref"] = baseRef,
                ["conflicting_paths"] = rebase.ConflictingPaths
            }, ct).ConfigureAwait(false);
            return $"{integrationBranch} forked from an older {baseRef} and rebasing it onto the " +
                $"current tip hit conflicts in: {paths}. No child was dispatched. Resolve the rebase " +
                $"manually (the accumulated work is safe on {integrationBranch}), or delete the " +
                $"branch to restart the chain from the current {baseRef}, then re-run.";
        }
        if (!rebase.Success)
        {
            await eventEmitterFactory().EmitChainGateFailureAsync(ticketId, "chain_refresh_rebase_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["base_ref"] = baseRef,
                ["detail"] = rebase.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"refreshing {integrationBranch} onto {baseRef} failed: {rebase.FailureReason}. " +
                $"No child was dispatched; the accumulated work is safe on {integrationBranch}.";
        }

        string refreshedSha;
        try { refreshedSha = await _git.HeadShaAsync(integrationWorktreePath, ct).ConfigureAwait(false); }
        catch { refreshedSha = "(unknown)"; }

        Console.WriteLine(
            $"[{ticketId}] {integrationBranch} was behind {baseRef}; rebased onto the current tip " +
            "before dispatching children.");
        await eventEmitterFactory().EmitAsync(
            EventKind.TicketWrite,
            ticketId,
            Phase.Chain,
            new Dictionary<string, object>
            {
                ["action"] = "chain_refresh_rebased",
                ["integration_branch"] = integrationBranch,
                ["base_ref"] = baseRef,
                ["old_tip"] = chainSha,
                ["new_tip"] = refreshedSha
            }, ct).ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Removes chain-owned worktrees after success while deliberately retaining their branches.
    /// Cleanup is advisory and never turns a successful chain into a failure.
    /// </summary>
    public async Task SweepChainWorktreesAsync(
        string ticketId,
        ChainEventEmitter eventEmitter,
        CancellationToken ct)
    {
        try
        {
            var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
            var decrufter = new WorktreeDecrufter(_git);
            var halted = new List<string>();
            foreach (var worktree in worktrees)
            {
                if (string.IsNullOrEmpty(worktree.Branch))
                    continue;
                if (!(worktree.Branch.StartsWith("ticket/", StringComparison.Ordinal)
                      || worktree.Branch.StartsWith("chain/", StringComparison.Ordinal)))
                    continue;
                var result = await decrufter.DecruftAsync(worktree.Path, _workingDirectory, ct).ConfigureAwait(false);
                if (result.HaltedAt is not null && result.HaltedAt != DecruftStep.WorktreeNotFound)
                    halted.Add($"{worktree.Path} (halted at {result.HaltedAt})");
            }
            if (halted.Count > 0)
            {
                await eventEmitter.EmitAsync(
                    EventKind.GateFailure,
                    ticketId,
                    Phase.Chain,
                    new Dictionary<string, object>
                    {
                        ["kind"] = "worktree_sweep_incomplete",
                        ["halted"] = halted.ToArray()
                    }, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cleanup must never fail a successful chain.
        }
    }

}
