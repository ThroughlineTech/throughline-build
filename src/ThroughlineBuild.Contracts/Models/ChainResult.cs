namespace ThroughlineBuild.Contracts.Models;

public record ChainResult(
    string TicketId,
    IReadOnlyList<ChainStep> Steps,
    ChainOutcome Outcome,
    TimeSpan TotalDuration,
    string? FinalRationale,
    SubsumedByEvidence? SubsumedBy = null,
    IReadOnlyList<ChainResult>? ChildResults = null);
