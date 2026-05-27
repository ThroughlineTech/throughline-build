namespace ThroughlineBuild.Contracts.Models;

public enum ChainOutcome
{
    Completed,
    StoppedAtPlan,
    StoppedAtImplement,
    StoppedAtReview,
    StoppedAtShip,
    ReworkCapExceeded,
    RefusedInitialState
}
