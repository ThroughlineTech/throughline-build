namespace ThroughlineBuild.Contracts.Models;

public sealed record ReviewFeedback(
    string Rationale,
    IReadOnlyList<string> ChecksFailed,
    int ReworkRoundNumber);
