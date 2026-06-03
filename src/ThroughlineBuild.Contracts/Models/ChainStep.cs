namespace ThroughlineBuild.Contracts.Models;

// IsStart marks a pre-run notice emitted through the OnStep stream just before a
// phase begins. Start markers are for console feedback only and are NOT added to a
// ChainResult's Steps list, so they carry no meaningful Status/Verdict/Duration.
public record ChainStep(
    string PhaseName,
    int ReworkRoundNumber,
    Status Status,
    string? FailureReason,
    VerdictKind? Verdict,
    TimeSpan Duration,
    string? PhaseSessionId,
    bool IsStart = false);
