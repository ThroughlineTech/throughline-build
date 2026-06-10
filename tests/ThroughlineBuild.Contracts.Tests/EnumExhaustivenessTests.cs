using Xunit;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

public class EnumExhaustivenessTests
{
    [Fact]
    public void Phase_HasExactlyExpectedValues()
    {
        var expected = new[] { Phase.Plan, Phase.Implement, Phase.Review, Phase.Ship, Phase.Chain, Phase.New, Phase.Command, Phase.Draft, Phase.Scaffold, Phase.Decompose, Phase.Gate };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<Phase>().OrderBy(x => (int)x));
    }

    [Fact]
    public void TicketState_HasExactlyExpectedValues()
    {
        var expected = new[] {
            TicketState.Backlog, TicketState.Planning, TicketState.Ready,
            TicketState.InProgress, TicketState.InReview, TicketState.Done, TicketState.Cancelled
        };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<TicketState>().OrderBy(x => (int)x));
    }

    [Fact]
    public void EventKind_HasExactlyExpectedValues()
    {
        var expected = new[] {
            EventKind.StateTransition, EventKind.LlmCall, EventKind.WorkerSpawn,
            EventKind.VerifierVerdict, EventKind.GateFailure, EventKind.TicketWrite,
            EventKind.ChainStart, EventKind.ChainEnd, EventKind.ReworkRound,
            EventKind.TicketSubsumed, EventKind.TargetAutoRebased,
            EventKind.DispatchStart, EventKind.DispatchEnd, EventKind.CostLedger
        };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<EventKind>().OrderBy(x => (int)x));
    }

    [Fact]
    public void Size_HasExactlyExpectedValues()
    {
        var expected = new[] { Size.S, Size.M, Size.L };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<Size>().OrderBy(x => (int)x));
    }

    [Fact]
    public void Risk_HasExactlyExpectedValues()
    {
        var expected = new[] { Risk.Low, Risk.Medium, Risk.High };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<Risk>().OrderBy(x => (int)x));
    }

    [Fact]
    public void Status_HasExactlyExpectedValues()
    {
        var expected = new[] { Status.Ok, Status.NeedsRework, Status.Failed, Status.Escalate };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<Status>().OrderBy(x => (int)x));
    }

    [Fact]
    public void VerdictKind_HasExactlyExpectedValues()
    {
        var expected = new[] { VerdictKind.Pass, VerdictKind.Rework, VerdictKind.Fail };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<VerdictKind>().OrderBy(x => (int)x));
    }

    [Fact]
    public void ChainOutcome_HasExactlyExpectedValues()
    {
        var expected = new[]
        {
            ChainOutcome.Completed,
            ChainOutcome.StoppedAtPlan,
            ChainOutcome.StoppedAtImplement,
            ChainOutcome.StoppedAtReview,
            ChainOutcome.StoppedAtShip,
            ChainOutcome.ReworkCapExceeded,
            ChainOutcome.RefusedInitialState,
            ChainOutcome.RefusedDirtyTree,
            ChainOutcome.RefusedWrongBranch,
            ChainOutcome.RatifiedObsolete,
            ChainOutcome.ParentCompleted,
            ChainOutcome.ParentStoppedEarly,
            ChainOutcome.ParentHasGrandchildren,
            ChainOutcome.Skipped,
            ChainOutcome.BatchImplemented,
            ChainOutcome.DryRunPreview,
            ChainOutcome.GateVacuous,
            ChainOutcome.ReviewUnavailable,
            ChainOutcome.GateEnvironmentFailure,
            ChainOutcome.TicketingUnavailable
        };
        Assert.Equal(expected.OrderBy(x => (int)x), Enum.GetValues<ChainOutcome>().OrderBy(x => (int)x));
    }
}
