namespace ThroughlineBuild.Helpers;

// Per-phase deterministic completion summary records. All fields are primitive
// strings / ints / bools so System.Text.Json serializes them without a custom
// JsonSerializerContext. Enum-typed source values (verdict, ship-failure-stage)
// are converted to ToString() at build time, by design.
//
// The discriminating "PhaseKind" string is stable user-visible text - keep in sync
// with the renderer's per-phase headers.

public abstract record PhaseSummary
{
    public string PhaseKind { get; init; } = string.Empty;
    public string TicketId { get; init; } = string.Empty;
    public bool Success { get; init; }
    // Failure path: populated only when Success is false.
    public string? FailedAt { get; init; }
    public string? FailureReason { get; init; }
    public string? SessionArtifactsPath { get; init; }
    public string? PlaneUrl { get; init; }
    // Optional worker stats (LlmCall event flattened). Null when no LlmCall event.
    public long? WorkerWallMs { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
}

public sealed record PlanPhaseSummary : PhaseSummary
{
    public string? SizeLabel { get; init; }
    public string? RiskLabel { get; init; }
    public string? StateFrom { get; init; }
    public string? StateTo { get; init; }
    public IReadOnlyList<string> PlaneOps { get; init; } = Array.Empty<string>();
    public string? InvestigatedAtSha { get; init; }
}

public sealed record ImplementPhaseSummary : PhaseSummary
{
    public string? BranchName { get; init; }
    public int CommitCount { get; init; }
    public IReadOnlyList<string> CommitShas { get; init; } = Array.Empty<string>();
    public int FilesChanged { get; init; }
    public int FilesAdded { get; init; }
    public int FilesEdited { get; init; }
    public int TestsAddedCount { get; init; }
    public string? CommitSha { get; init; }
    public string? StateFrom { get; init; }
    public string? StateTo { get; init; }
}

public sealed record ReviewPhaseSummary : PhaseSummary
{
    public string? Verdict { get; init; }
    public string? VerdictRationale { get; init; }
    public IReadOnlyList<string> ChecksFailed { get; init; } = Array.Empty<string>();
    public string? StateFrom { get; init; }
    public string? StateTo { get; init; }
}

public sealed record ShipPhaseSummary : PhaseSummary
{
    public string? BranchName { get; init; }
    public string? MergedSha { get; init; }
    public int FilesChanged { get; init; }
    public string? StateFrom { get; init; }
    public string? StateTo { get; init; }
    public bool Archived { get; init; }
}
