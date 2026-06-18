using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Contracts.Models;

public sealed record ReviewFeedback(
    string Rationale,
    IReadOnlyList<string> ChecksFailed,
    int ReworkRoundNumber,
    // Non-null only for gate-originated rework; null for review-originated rework.
    IReadOnlyList<CheckResult>? GateFailedChecks = null,
    // Raw results for the checks named in ChecksFailed, when the caller has them (review- or
    // recheck-originated rework). Rendered into the rework brief verbatim (command line, exit
    // code, output tails) so the worker fixes against the check's own output, not the
    // reviewer's interpretation of the tool's rules - which can be confidently wrong.
    IReadOnlyList<CheckResult>? FailedCheckDetails = null);
