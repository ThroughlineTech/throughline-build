namespace ThroughlineBuild.Contracts.Models;

public record SubsumedByEvidence(string Commit, IReadOnlyList<string> Files, string Rationale);
