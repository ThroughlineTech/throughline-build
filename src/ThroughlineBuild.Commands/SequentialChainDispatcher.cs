using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Dispatches a list of ticket IDs sequentially, applying the AncestorSkipFilter
/// skip rule when continuePastFailure is false. When continuePastFailure is false and a
/// prior ticket in the sequence produced a non-success outcome, all remaining tickets
/// receive a synthesized Skipped result and are not dispatched.
///
/// This is the fallback sequential implementation for multi-ticket chain dispatch.
/// TLB-312 will replace the call site in Program.cs with concurrent ParallelDispatcher
/// once that branch is rebased onto main.
/// </summary>
public static class SequentialChainDispatcher
{
    /// <summary>
    /// Runs each ticket in <paramref name="ticketIds"/> sequentially. Returns one
    /// <see cref="ChainResult"/> per ticket in input order.
    /// </summary>
    /// <param name="ticketIds">Ordered list of ticket IDs to process.</param>
    /// <param name="runTicket">
    /// Async delegate that runs a single ticket chain and returns its result.
    /// </param>
    /// <param name="continuePastFailure">
    /// When false, any ticket whose predecessor(s) failed is skipped rather than
    /// dispatched to <paramref name="runTicket"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IReadOnlyList<ChainResult>> RunAsync(
        IReadOnlyList<string> ticketIds,
        Func<string, CancellationToken, Task<ChainResult>> runTicket,
        bool continuePastFailure,
        CancellationToken ct)
    {
        var orderedIds = ticketIds is List<string> l ? l : new List<string>(ticketIds);

        // Build implicit linear edges: each preceding ticket is an ancestor of all following ones.
        var edges = new List<(string Blocker, string Blocked)>();
        for (int i = 0; i < orderedIds.Count; i++)
            for (int j = i + 1; j < orderedIds.Count; j++)
                edges.Add((orderedIds[i], orderedIds[j]));

        var results = new List<ChainResult>(ticketIds.Count);
        var completedResults = new Dictionary<string, ChainResult>(StringComparer.Ordinal);

        foreach (var tid in orderedIds)
        {
            ct.ThrowIfCancellationRequested();

            var skipResult = AncestorSkipFilter.ShouldSkip(tid, completedResults, edges, continuePastFailure);
            if (skipResult != null)
            {
                results.Add(skipResult);
                completedResults[tid] = skipResult;
                continue;
            }

            var result = await runTicket(tid, ct).ConfigureAwait(false);
            results.Add(result);
            completedResults[tid] = result;
        }

        return results;
    }
}
