namespace ThroughlineBuild.Contracts.Models;

public record ChainStep(
    string PhaseName,
    int ReworkRoundNumber,
    Status Status,
    string? FailureReason,
    VerdictKind? Verdict,
    TimeSpan Duration,
    string? PhaseSessionId);
