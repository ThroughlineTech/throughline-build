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
    RefusedDirtyTree,   // working tree not clean at chain start (conflict or unrelated stash) - refused before planning
    RefusedWrongBranch, // main worktree not on the ship target branch at chain start - refused before planning
    RatifiedObsolete,
    ParentCompleted,    // all eligible children completed
    ParentStoppedEarly, // one or more children stopped before completing
    ParentHasGrandchildren, // tree is deeper than one level; chain the intermediate ticket directly
    Skipped             // ticket skipped because an ancestor failed and continuePastFailure is false
}
