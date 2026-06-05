namespace ThroughlineBuild.Contracts.Models;

public record ParallelDispatchResult(
    bool Success,
    IReadOnlyList<ChainResult> Results,
    string? FailureReason,
    ChainOutcome? PreservedOutcome = null);
