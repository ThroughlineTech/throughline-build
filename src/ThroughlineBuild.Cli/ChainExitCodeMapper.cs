using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

public static class ChainExitCodeMapper
{
    public static int GetExitCode(ParallelDispatchResult result) => result.Success
        ? 0
        : result.PreservedOutcome is { } outcome
            ? GetExitCode(outcome)
            : 1;

    public static int GetExitCode(ChainOutcome outcome) => outcome switch
    {
        ChainOutcome.Completed => 0,
        ChainOutcome.RatifiedObsolete => 0,
        ChainOutcome.ParentCompleted => 0,
        ChainOutcome.RefusedInitialState => 2,
        ChainOutcome.RefusedDirtyTree => 2,
        ChainOutcome.ParentHasGrandchildren => 2,
        ChainOutcome.StoppedAtPlan => 3,
        ChainOutcome.ParentStoppedEarly => 3,
        ChainOutcome.Skipped => 3,
        ChainOutcome.StoppedAtImplement => 4,
        ChainOutcome.StoppedAtReview => 5,
        ChainOutcome.ReworkCapExceeded => 6,
        ChainOutcome.StoppedAtShip => 7,
        _ => 1
    };
}
