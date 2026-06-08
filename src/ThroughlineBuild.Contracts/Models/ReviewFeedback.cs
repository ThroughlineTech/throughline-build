using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Contracts.Models;

public sealed record ReviewFeedback(
    string Rationale,
    IReadOnlyList<string> ChecksFailed,
    int ReworkRoundNumber,
    // Non-null only for gate-originated rework; null for review-originated rework.
    IReadOnlyList<CheckResult>? GateFailedChecks = null);
