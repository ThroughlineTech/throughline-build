namespace ThroughlineBuild.Contracts.Models;

public record Verdict(
    VerdictKind Kind,
    string Rationale,
    IReadOnlyList<string> ChecksFailed);

public enum VerdictKind { Pass, Rework, Fail }
