namespace ThroughlineBuild.Contracts.Models;

public enum ChainOutcome
{
    Completed,
    StoppedAtPlan,
    StoppedAtImplement,
    StoppedAtReview,
    StoppedAtShip,
    ReworkCapExceeded,
    RefusedInitialState,
    RatifiedObsolete,
    ParentCompleted,    // all eligible children completed
    ParentStoppedEarly, // one or more children stopped before completing
    Skipped             // ticket skipped because an ancestor failed and continuePastFailure is false
}
