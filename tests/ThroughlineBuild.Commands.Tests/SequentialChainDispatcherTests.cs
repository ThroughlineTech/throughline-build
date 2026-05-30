using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

/// <summary>
/// Tests for SequentialChainDispatcher - the multi-ticket sequential fallback
/// used by the "build chain" verb when multiple ticket IDs are supplied.
/// </summary>
public class SequentialChainDispatcherTests
{
    private static ChainResult MakeResult(string ticketId, ChainOutcome outcome) =>
        new ChainResult(
            TicketId: ticketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: TimeSpan.FromSeconds(1),
            FinalRationale: null);

    private static Func<string, CancellationToken, Task<ChainResult>> MakeRunner(
        Dictionary<string, ChainOutcome> outcomes,
        List<string> dispatched)
    {
        return (tid, ct) =>
        {
            dispatched.Add(tid);
            var outcome = outcomes.TryGetValue(tid, out var o) ? o : ChainOutcome.Completed;
            return Task.FromResult(MakeResult(tid, outcome));
        };
    }

    [Fact]
    public async Task RunAsync_ContinuePastFailureFalse_SkipsDescendantWhenAncestorFailed()
    {
        // TLB-A fails -> TLB-B should NOT be dispatched; result is Skipped.
        var outcomes = new Dictionary<string, ChainOutcome>
        {
            ["TLB-A"] = ChainOutcome.StoppedAtPlan,
            ["TLB-B"] = ChainOutcome.Completed
        };
        var dispatched = new List<string>();
        var runner = MakeRunner(outcomes, dispatched);

        var results = await SequentialChainDispatcher.RunAsync(
            ticketIds: new[] { "TLB-A", "TLB-B" },
            runTicket: runner,
            continuePastFailure: false,
            ct: CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(ChainOutcome.StoppedAtPlan, results[0].Outcome);
        Assert.Equal(ChainOutcome.Skipped, results[1].Outcome);
        Assert.Equal("TLB-B", results[1].TicketId);
        Assert.NotNull(results[1].SkipReason);
        Assert.Contains("failed", results[1].SkipReason);

        // TLB-B must NOT have been dispatched to the runner.
        Assert.DoesNotContain("TLB-B", dispatched);
    }

    [Fact]
    public async Task RunAsync_ContinuePastFailureTrue_DispatchesDescendantEvenWhenAncestorFailed()
    {
        // TLB-A fails -> TLB-B IS dispatched because continuePastFailure=true.
        var outcomes = new Dictionary<string, ChainOutcome>
        {
            ["TLB-A"] = ChainOutcome.StoppedAtPlan,
            ["TLB-B"] = ChainOutcome.Completed
        };
        var dispatched = new List<string>();
        var runner = MakeRunner(outcomes, dispatched);

        var results = await SequentialChainDispatcher.RunAsync(
            ticketIds: new[] { "TLB-A", "TLB-B" },
            runTicket: runner,
            continuePastFailure: true,
            ct: CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(ChainOutcome.StoppedAtPlan, results[0].Outcome);
        Assert.Equal(ChainOutcome.Completed, results[1].Outcome);

        // TLB-B must have been dispatched.
        Assert.Contains("TLB-B", dispatched);
    }

    [Fact]
    public async Task RunAsync_AllCompleted_ReturnsAllCompletedResults()
    {
        var dispatched = new List<string>();
        var outcomes = new Dictionary<string, ChainOutcome>
        {
            ["TLB-1"] = ChainOutcome.Completed,
            ["TLB-2"] = ChainOutcome.Completed,
            ["TLB-3"] = ChainOutcome.Completed
        };
        var runner = MakeRunner(outcomes, dispatched);

        var results = await SequentialChainDispatcher.RunAsync(
            ticketIds: new[] { "TLB-1", "TLB-2", "TLB-3" },
            runTicket: runner,
            continuePastFailure: false,
            ct: CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
        Assert.Equal(new[] { "TLB-1", "TLB-2", "TLB-3" }, dispatched);
    }

    [Fact]
    public async Task RunAsync_ResultsPreserveInputOrder()
    {
        // Verify output list is in the same order as input regardless of outcome.
        var outcomes = new Dictionary<string, ChainOutcome>
        {
            ["TLB-X"] = ChainOutcome.StoppedAtImplement,
            ["TLB-Y"] = ChainOutcome.Completed
        };
        var dispatched = new List<string>();
        var runner = MakeRunner(outcomes, dispatched);

        var results = await SequentialChainDispatcher.RunAsync(
            ticketIds: new[] { "TLB-X", "TLB-Y" },
            runTicket: runner,
            continuePastFailure: true,
            ct: CancellationToken.None);

        Assert.Equal("TLB-X", results[0].TicketId);
        Assert.Equal("TLB-Y", results[1].TicketId);
    }

    [Fact]
    public async Task RunAsync_ContinuePastFailureFalse_SkipsAllRemainingAfterFirstFailure()
    {
        // TLB-1 succeeds, TLB-2 fails, TLB-3 and TLB-4 should both be skipped.
        var outcomes = new Dictionary<string, ChainOutcome>
        {
            ["TLB-1"] = ChainOutcome.Completed,
            ["TLB-2"] = ChainOutcome.StoppedAtReview,
            ["TLB-3"] = ChainOutcome.Completed,
            ["TLB-4"] = ChainOutcome.Completed
        };
        var dispatched = new List<string>();
        var runner = MakeRunner(outcomes, dispatched);

        var results = await SequentialChainDispatcher.RunAsync(
            ticketIds: new[] { "TLB-1", "TLB-2", "TLB-3", "TLB-4" },
            runTicket: runner,
            continuePastFailure: false,
            ct: CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(ChainOutcome.Completed, results[0].Outcome);
        Assert.Equal(ChainOutcome.StoppedAtReview, results[1].Outcome);
        Assert.Equal(ChainOutcome.Skipped, results[2].Outcome);
        Assert.Equal(ChainOutcome.Skipped, results[3].Outcome);

        Assert.DoesNotContain("TLB-3", dispatched);
        Assert.DoesNotContain("TLB-4", dispatched);
    }
}
