namespace ThroughlineBuild.Contracts.Models;

public record ReviewFeedback(
    string Rationale,
    IReadOnlyList<string> ChecksFailed,
    int ReworkRoundNumber);
