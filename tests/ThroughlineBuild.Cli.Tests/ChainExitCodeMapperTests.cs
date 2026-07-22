using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ChainExitCodeMapperTests
{
    [Fact]
    public void MultiTicketDirtyTreeRefusal_MapsToPreflightRefusalExitCode()
    {
        var results = new[]
        {
            MakeDirtyResult("TLB-455"),
            MakeDirtyResult("TLB-456"),
            MakeDirtyResult("TLB-457"),
            MakeDirtyResult("TLB-458")
        };
        var dispatchResult = new ParallelDispatchResult(
            Success: false,
            Results: results,
            FailureReason: "ticket TLB-455 stopped with outcome RefusedDirtyTree",
            PreservedOutcome: ChainOutcome.RefusedDirtyTree);

        Assert.Equal(2, ChainExitCodeMapper.GetExitCode(dispatchResult));
    }

    [Fact]
    public void WrongBranchRefusal_MapsToPreflightRefusalExitCode()
    {
        Assert.Equal(2, ChainExitCodeMapper.GetExitCode(ChainOutcome.RefusedWrongBranch));
    }

    [Fact]
    public void GateVacuous_MapsToDedicatedExitCode()
    {
        Assert.Equal(8, ChainExitCodeMapper.GetExitCode(ChainOutcome.GateVacuous));
    }

    [Fact]
    public void ReviewUnavailable_MapsToDedicatedExitCode()
    {
        // Distinct from StoppedAtReview (5): a provider block is not a review rejection. See TLB-527.
        Assert.Equal(9, ChainExitCodeMapper.GetExitCode(ChainOutcome.ReviewUnavailable));
        Assert.NotEqual(
            ChainExitCodeMapper.GetExitCode(ChainOutcome.StoppedAtReview),
            ChainExitCodeMapper.GetExitCode(ChainOutcome.ReviewUnavailable));
    }

    [Fact]
    public void GateEnvironmentFailure_MapsToDedicatedExitCode()
    {
        // Distinct from GateVacuous (8) and the generic stop codes: the operator's next action
        // (fix the environment, re-run everything) is different. See TLB-538.
        Assert.Equal(10, ChainExitCodeMapper.GetExitCode(ChainOutcome.GateEnvironmentFailure));
    }

    [Fact]
    public void MultiTicketEnvironmentFailure_PreservedOutcome_MapsToDedicatedExitCode()
    {
        var dispatchResult = new ParallelDispatchResult(
            Success: false,
            Results: new[]
            {
                new ChainResult("TLB-24", Array.Empty<ChainStep>(), ChainOutcome.GateEnvironmentFailure,
                    TimeSpan.Zero, "gate: build failed - environment failure"),
                new ChainResult("TLB-28", Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                    TimeSpan.Zero, null, SkipReason: "environment gate failure in TLB-24")
            },
            FailureReason: "ticket TLB-24 stopped with outcome GateEnvironmentFailure",
            PreservedOutcome: ChainOutcome.GateEnvironmentFailure);

        Assert.Equal(10, ChainExitCodeMapper.GetExitCode(dispatchResult));
    }

    [Fact]
    public void TicketingUnavailable_MapsToDedicatedExitCode()
    {
        // Distinct from the ticket-attributable stop codes: the operator's next action
        // (restore connectivity, re-run) is environmental. See TLB-545.
        Assert.Equal(11, ChainExitCodeMapper.GetExitCode(ChainOutcome.TicketingUnavailable));
    }

    [Fact]
    public void MultiTicketTicketingUnavailable_PreservedOutcome_MapsToDedicatedExitCode()
    {
        var dispatchResult = new ParallelDispatchResult(
            Success: false,
            Results: new[]
            {
                new ChainResult("TLB-26", Array.Empty<ChainStep>(), ChainOutcome.TicketingUnavailable,
                    TimeSpan.Zero, "Plane API unreachable (POST .../comments/, attempt 4): nodename nor servname provided"),
                new ChainResult("TLB-27", Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                    TimeSpan.Zero, null, SkipReason: "ticketing backend unreachable in TLB-26; restore connectivity and re-run")
            },
            FailureReason: "ticket TLB-26 stopped with outcome TicketingUnavailable",
            PreservedOutcome: ChainOutcome.TicketingUnavailable);

        Assert.Equal(11, ChainExitCodeMapper.GetExitCode(dispatchResult));
    }

    [Fact]
    public void ParentWithTicketingUnavailableChild_MapsToDedicatedExitCode()
    {
        var child = new ChainResult(
            "TLB-2", Array.Empty<ChainStep>(), ChainOutcome.TicketingUnavailable,
            TimeSpan.Zero, "backend unavailable");
        var parent = new ChainResult(
            "TLB-1", Array.Empty<ChainStep>(), ChainOutcome.ParentStoppedEarly,
            TimeSpan.Zero, "child stopped", ChildResults: new[] { child });

        Assert.Equal(11, ChainExitCodeMapper.GetExitCode(parent));
    }

    private static ChainResult MakeDirtyResult(string ticketId) => new(
        TicketId: ticketId,
        Steps: Array.Empty<ChainStep>(),
        Outcome: ChainOutcome.RefusedDirtyTree,
        TotalDuration: TimeSpan.Zero,
        FinalRationale: "main worktree has tracked changes",
        DirtyTreeCause: DirtyTreeCause.TrackedChanges);
}
