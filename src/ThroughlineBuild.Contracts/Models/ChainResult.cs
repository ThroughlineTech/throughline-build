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
    IReadOnlyList<string>? ShippedProvides = null);
