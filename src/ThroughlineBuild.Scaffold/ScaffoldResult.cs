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
    bool WasDryRun,
    string? OpTicketId = null,
    IReadOnlyList<DependencyEdge>? DependencyEdges = null,
    IReadOnlyList<ScaffoldedEntity>? CreatedEntities = null);

/// <summary>
/// Which role a created ticket plays in the scaffold tree.
/// </summary>
public enum ScaffoldedEntityKind { Operation, Plan, Brief }

/// <summary>
/// A single ticket that was actually created during scaffolding, carrying the real
/// backend ticket id correlated directly with its plan/brief identity. The CLI drives
/// its "Created ..." output from this list so it can never print an id it did not get
/// back from the backend (no "?" placeholders).
/// </summary>
/// <param name="Kind">Operation, Plan, or Brief.</param>
/// <param name="TicketId">Human-readable backend id (e.g. "TLB-102"). Always a real id.</param>
/// <param name="DisplayName">Operation title, plan name, or brief slug.</param>
/// <param name="PlanId">The op-doc plan letter for Plan/Brief entities; null for Operation.</param>
/// <param name="ParentTicketId">
/// Parent ticket id: the op id for a Plan, the plan id for a Brief, null/empty if the
/// parent could not be linked (orphan). Null for Operation.
/// </param>
public record ScaffoldedEntity(
    ScaffoldedEntityKind Kind,
    string TicketId,
    string DisplayName,
    string? PlanId,
    string? ParentTicketId);

/// <summary>
/// A single blocked_by relation created during scaffolding.
/// </summary>
/// <param name="BlockedId">Ticket ID of the blocked (dependent) ticket.</param>
/// <param name="BlockerId">Ticket ID of the blocker (dependency) ticket.</param>
public record DependencyEdge(string BlockedId, string BlockerId);

/// <summary>
/// Records a single per-step failure during scaffolding.
/// </summary>
/// <param name="Stage">
/// Identifies which step failed, e.g. "plan_A_create", "brief_A_02_create", "brief_A_02_parent_link".
/// </param>
/// <param name="Detail">Exception message or description of what went wrong.</param>
public record ScaffoldFailure(string Stage, string Detail);
