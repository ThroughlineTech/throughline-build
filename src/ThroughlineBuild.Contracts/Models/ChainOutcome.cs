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
    Skipped,            // ticket skipped because an ancestor failed and continuePastFailure is false
    BatchImplemented,   // ticket built, reviewed, and shipped into the integration branch as part of a warm batch session
    DryRunPreview,      // ticket was included in a dry-run schedule; no phases executed
    GateVacuous,        // a gating check could not be proven to fail on broken input (vacuous), or its canary could not be cleaned up - a config/setup defect; hard-fail without rework
    ReviewUnavailable,  // the verifier worker was blocked by a transient provider error (quota/rate-limit/auth); review never ran - NOT a review rejection; ticket left cleanly InReview and resumable via 'build review <id>'. See TLB-527.
    GateEnvironmentFailure // a gating check fails on the ticket worktree AND on the untouched base ref - the environment/config is broken, not the ticket's code; hard-fail without rework, remaining siblings/roots are skipped. Ticket left InReview and resumable. See TLB-538.
}
