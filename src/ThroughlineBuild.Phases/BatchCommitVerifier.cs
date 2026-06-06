using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Verifies that the shared worktree is clean after a batch session and that
/// each reported commit SHA exists in the branch in the declared stack order.
///
/// The conductor must not trust the worker's self-reported SHAs alone: this
/// class re-derives the commit attribution from actual git state so a worker
/// that misreports, skips, or reorders commits is caught before any marker
/// is posted.
/// </summary>
internal static class BatchCommitVerifier
{
    /// <summary>
    /// Outcome of a batch commit verification run.
    /// On success <see cref="ConfirmedTickets"/> contains the per-ticket results
    /// sorted by <c>stack_position</c> ascending, sourced from verified git state.
    /// On failure <see cref="FailureReason"/> names the first ticket that failed.
    /// </summary>
    internal sealed record VerifyResult(
        bool Success,
        string? FailureReason,
        IReadOnlyList<BatchTicketResult> ConfirmedTickets);

    /// <summary>
    /// Confirms the shared worktree is clean, then checks that each reported
    /// commit SHA is present in <c><paramref name="baseRef"/>..HEAD</c> and
    /// that the SHAs appear in the order declared by <c>stack_position</c>
    /// (ascending = oldest first).
    ///
    /// Returns a confirmed ticket list on pass. On failure, names the first
    /// ticket that does not satisfy the contract.
    /// </summary>
    internal static async Task<VerifyResult> VerifyAsync(
        IGitClient git,
        string worktreePath,
        string baseRef,
        IReadOnlyList<BatchTicketResult>? reportedTickets,
        CancellationToken ct)
    {
        // Step 1: Confirm the worktree is clean after the session.
        var dirty = await WorkingTreeHygieneGate
            .DirtyFilesCheckAsync(git, worktreePath, ct)
            .ConfigureAwait(false);
        if (dirty.Count > 0)
        {
            var sample = string.Join(", ", dirty.Take(5));
            var more = dirty.Count > 5 ? $" (+{dirty.Count - 5} more)" : "";
            return Fail($"worktree is dirty after batch session: {sample}{more}");
        }

        // No reported tickets - nothing further to verify.
        if (reportedTickets is null || reportedTickets.Count == 0)
            return new VerifyResult(true, null, Array.Empty<BatchTicketResult>());

        // Step 2: Get the actual commits the batch session produced (newest first).
        var actualShas = await git
            .LogShasAsync($"{baseRef}..HEAD", 0, worktreePath, ct)
            .ConfigureAwait(false);

        // Fail closed: if git cannot report the commits we cannot confirm them.
        if (actualShas.Count == 0)
            return Fail(
                "batch commit verification failed: git reported no commits since base; " +
                "cannot confirm reported SHAs exist in the branch");

        // Build sha -> index map (case-insensitive). Lower index = newer (closer to HEAD).
        var shaToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < actualShas.Count; i++)
        {
            if (!string.IsNullOrEmpty(actualShas[i]))
                shaToIndex.TryAdd(actualShas[i], i);
        }

        // Step 3: Sort reported tickets by stack_position ascending (oldest first).
        var sortedTickets = reportedTickets
            .OrderBy(t => t.StackPosition)
            .ToList();

        // Step 4: Verify existence and order.
        //
        // In a newest-first log a commit with lower stack_position (older) must appear
        // at a HIGHER index than a commit with higher stack_position (newer).
        // We walk in ascending stack_position order and expect each entry's actual index
        // to be strictly less than the previous entry's (closer to HEAD = smaller index).
        //
        // Sentinel: int.MaxValue means there is no previous entry, so the first ticket
        // skips the order check (prevActualIndex <= actualIndex is always false when
        // prevActualIndex is MaxValue and actualIndex is a non-negative int).
        int prevActualIndex = int.MaxValue;
        for (int i = 0; i < sortedTickets.Count; i++)
        {
            var ticket = sortedTickets[i];

            if (!shaToIndex.TryGetValue(ticket.CommitSha, out var actualIndex))
                return Fail(
                    $"ticket {ticket.TicketId}: commit {ticket.CommitSha} " +
                    $"(stack_position {ticket.StackPosition}) is not present in the branch");

            if (i > 0 && prevActualIndex <= actualIndex)
                return Fail(
                    $"ticket {ticket.TicketId}: commit {ticket.CommitSha} " +
                    $"(stack_position {ticket.StackPosition}) does not follow " +
                    $"ticket {sortedTickets[i - 1].TicketId} commit " +
                    $"{sortedTickets[i - 1].CommitSha} " +
                    $"(stack_position {sortedTickets[i - 1].StackPosition}); " +
                    "commits are out of declared order");

            prevActualIndex = actualIndex;
        }

        return new VerifyResult(true, null, sortedTickets.AsReadOnly());
    }

    private static VerifyResult Fail(string reason) =>
        new(false, reason, Array.Empty<BatchTicketResult>());
}
