namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Outcome of a ScaffoldPhase.RunAsync invocation.
/// </summary>
public record ScaffoldResult(
    int PlansCreated,
    int BriefsCreated,
    IReadOnlyList<string> CreatedTicketIds,
    IReadOnlyList<ScaffoldFailure> Failures,
    bool WasAbortedByParseErrors,
    bool WasAbortedByValidationErrors,
    bool WasBlockedByWarnings,
    bool WasDryRun);

/// <summary>
/// Records a single per-step failure during scaffolding.
/// </summary>
/// <param name="Stage">
/// Identifies which step failed, e.g. "plan_A_create", "brief_A_02_create", "brief_A_02_parent_link".
/// </param>
/// <param name="Detail">Exception message or description of what went wrong.</param>
public record ScaffoldFailure(string Stage, string Detail);
