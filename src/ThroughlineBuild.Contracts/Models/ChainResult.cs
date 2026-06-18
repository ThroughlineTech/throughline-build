namespace ThroughlineBuild.Contracts.Models;

public record ChainResult(
    string TicketId,
    IReadOnlyList<ChainStep> Steps,
    ChainOutcome Outcome,
    TimeSpan TotalDuration,
    string? FinalRationale,
    SubsumedByEvidence? SubsumedBy = null,
    IReadOnlyList<ChainResult>? ChildResults = null,
    string? SkipReason = null,
    DirtyTreeCause? DirtyTreeCause = null,
    // Provides declared by the completion claim of the shipped ticket.
    // Populated only for leaf tickets with Outcome=Completed; null otherwise.
    // Used by ChainPhase to accumulate upstream provides for the consumes-provides preflight.
    IReadOnlyList<string>? ShippedProvides = null)
{
    // True when this result - or any descendant in a parent chain - stopped with
    // GateEnvironmentFailure. An environment failure is global to the machine/config,
    // so dispatch layers use this to skip remaining siblings/roots instead of running
    // every one of them into the same wall (TLB-538).
    public bool ContainsEnvironmentFailure() =>
        Outcome == ChainOutcome.GateEnvironmentFailure
        || (ChildResults is not null && ChildResults.Any(c => c.ContainsEnvironmentFailure()));

    // True when this result - or any descendant - stopped with TicketingUnavailable: the
    // ticketing backend itself was unreachable, so siblings/roots would fail identically (TLB-545).
    public bool ContainsTicketingUnavailable() =>
        Outcome == ChainOutcome.TicketingUnavailable
        || (ChildResults is not null && ChildResults.Any(c => c.ContainsTicketingUnavailable()));

    // Umbrella for the two environmental stop classes (broken gate environment, unreachable
    // ticketing backend). Both are global to the machine, not the ticket's fault, so dispatch
    // layers consult this one predicate when deciding to skip remaining siblings/roots.
    public bool ContainsEnvironmentalStop() =>
        ContainsEnvironmentFailure() || ContainsTicketingUnavailable();
}
