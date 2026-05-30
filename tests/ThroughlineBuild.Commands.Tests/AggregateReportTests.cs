using System.IO;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

/// <summary>
/// Tests for PrintAggregateReport (ChainCommand) and AncestorSkipFilter.ShouldSkip.
/// </summary>
[Collection("CommandConsoleTests")]
public class AggregateReportTests
{
    // Helper to run PrintAggregateReport and capture stdout.
    private static string CaptureAggregateReport(IReadOnlyList<ChainResult> results)
    {
        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            ChainCommand.PrintAggregateReport(results);
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static ChainResult MakeResult(
        string ticketId,
        ChainOutcome outcome,
        double seconds = 1.0,
        string? finalRationale = null,
        string? skipReason = null) =>
        new ChainResult(
            TicketId: ticketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: TimeSpan.FromSeconds(seconds),
            FinalRationale: finalRationale,
            SkipReason: skipReason);

    // --- PrintAggregateReport tests ---

    [Fact]
    public void PrintAggregateReport_AllCompleted_ShowsCorrectSummaryLine()
    {
        var results = new[]
        {
            MakeResult("TLB-1", ChainOutcome.Completed, 2.0),
            MakeResult("TLB-2", ChainOutcome.Completed, 3.0),
            MakeResult("TLB-3", ChainOutcome.Completed, 1.0),
        };

        var output = CaptureAggregateReport(results);

        Assert.Contains("3 tickets: 3 completed, 0 failed, 0 skipped", output);
        Assert.Contains("[TLB-1] Completed", output);
        Assert.Contains("[TLB-2] Completed", output);
        Assert.Contains("[TLB-3] Completed", output);
        Assert.Contains("--- aggregate report ---", output);
    }

    [Fact]
    public void PrintAggregateReport_OneFailedOneSkipped_ShowsCorrectSummaryLine()
    {
        var results = new[]
        {
            MakeResult("TLB-A", ChainOutcome.Completed, 2.1),
            MakeResult("TLB-B", ChainOutcome.StoppedAtPlan, 0.5, finalRationale: "reason here"),
            MakeResult("TLB-C", ChainOutcome.Skipped, 0.0, skipReason: "skipped (ancestor TLB-B failed)"),
        };

        var output = CaptureAggregateReport(results);

        Assert.Contains("3 tickets: 1 completed, 1 failed, 1 skipped", output);
        Assert.Contains("[TLB-A] Completed", output);
        Assert.Contains("[TLB-B] Failed", output);
        Assert.Contains("reason here", output);
        Assert.Contains("[TLB-C] Skipped", output);
        Assert.Contains("skipped (ancestor TLB-B failed)", output);
    }

    [Fact]
    public void PrintAggregateReport_OrderIsInputStable()
    {
        // Report lines must appear in the same order as the input list,
        // not sorted by outcome or any other reordering.
        var results = new[]
        {
            MakeResult("TLB-3", ChainOutcome.Skipped),
            MakeResult("TLB-1", ChainOutcome.Completed),
            MakeResult("TLB-2", ChainOutcome.StoppedAtImplement),
        };

        var output = CaptureAggregateReport(results);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the line indices (skip the header line at index 0).
        int idx3 = Array.FindIndex(lines, l => l.Contains("[TLB-3]"));
        int idx1 = Array.FindIndex(lines, l => l.Contains("[TLB-1]"));
        int idx2 = Array.FindIndex(lines, l => l.Contains("[TLB-2]"));

        Assert.True(idx3 >= 0, "expected TLB-3 line");
        Assert.True(idx1 >= 0, "expected TLB-1 line");
        Assert.True(idx2 >= 0, "expected TLB-2 line");

        // Input order: TLB-3, TLB-1, TLB-2.
        Assert.True(idx3 < idx1, "TLB-3 should appear before TLB-1");
        Assert.True(idx1 < idx2, "TLB-1 should appear before TLB-2");
    }

    // --- AncestorSkipFilter tests ---

    private static readonly IEnumerable<(string Blocker, string Blocked)> NoEdges =
        Array.Empty<(string, string)>();

    [Fact]
    public void ShouldSkip_NoAncestorFailed_ReturnsNull()
    {
        var completed = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["TLB-A"] = MakeResult("TLB-A", ChainOutcome.Completed)
        };
        var edges = new[] { ("TLB-A", "TLB-B") };

        var result = AncestorSkipFilter.ShouldSkip(
            ticketId: "TLB-B",
            completedResults: completed,
            edges: edges,
            continuePastFailure: false);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkip_AncestorFailed_ReturnsSynthesizedSkippedResult()
    {
        var completed = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["TLB-A"] = MakeResult("TLB-A", ChainOutcome.StoppedAtPlan)
        };
        var edges = new[] { ("TLB-A", "TLB-B") };

        var result = AncestorSkipFilter.ShouldSkip(
            ticketId: "TLB-B",
            completedResults: completed,
            edges: edges,
            continuePastFailure: false);

        Assert.NotNull(result);
        Assert.Equal("TLB-B", result!.TicketId);
        Assert.Equal(ChainOutcome.Skipped, result.Outcome);
        Assert.NotNull(result.SkipReason);
        Assert.Contains("TLB-A", result.SkipReason);
        Assert.Contains("failed", result.SkipReason);
    }

    [Fact]
    public void ShouldSkip_ContinuePastFailure_ReturnsNullEvenIfAncestorFailed()
    {
        var completed = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["TLB-A"] = MakeResult("TLB-A", ChainOutcome.StoppedAtPlan)
        };
        var edges = new[] { ("TLB-A", "TLB-B") };

        var result = AncestorSkipFilter.ShouldSkip(
            ticketId: "TLB-B",
            completedResults: completed,
            edges: edges,
            continuePastFailure: true);

        Assert.Null(result);
    }
}
