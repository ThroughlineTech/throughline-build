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

    private static ChainResult MakeDirtyResult(string ticketId) => new(
        TicketId: ticketId,
        Steps: Array.Empty<ChainStep>(),
        Outcome: ChainOutcome.RefusedDirtyTree,
        TotalDuration: TimeSpan.Zero,
        FinalRationale: "main worktree has tracked changes",
        DirtyTreeCause: DirtyTreeCause.TrackedChanges);
}
