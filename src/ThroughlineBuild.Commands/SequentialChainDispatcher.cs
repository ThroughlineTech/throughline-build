using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Dispatches a list of ticket IDs sequentially, applying a simple predecessor-failure
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
    private static readonly IReadOnlySet<ChainOutcome> SuccessOutcomes =
        new HashSet<ChainOutcome>
        {
            ChainOutcome.Completed,
            ChainOutcome.RatifiedObsolete,
            ChainOutcome.ParentCompleted
        };

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
        var results = new List<ChainResult>(ticketIds.Count);
        bool anyFailedSoFar = false;

        foreach (var tid in ticketIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!continuePastFailure && anyFailedSoFar)
            {
                results.Add(new ChainResult(
                    TicketId: tid,
                    Steps: Array.Empty<ChainStep>(),
                    Outcome: ChainOutcome.Skipped,
                    TotalDuration: TimeSpan.Zero,
                    FinalRationale: null,
                    SkipReason: "skipped (prior ticket in sequence failed)"));
                continue;
            }

            var result = await runTicket(tid, ct).ConfigureAwait(false);
            results.Add(result);

            if (!SuccessOutcomes.Contains(result.Outcome))
                anyFailedSoFar = true;
        }

        return results;
    }
}
