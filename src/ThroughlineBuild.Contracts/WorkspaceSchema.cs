namespace ThroughlineBuild.Contracts;

using ThroughlineBuild.Contracts.Models;

/// <summary>
/// The canonical set of Plane states and labels the build workflow assumes a project has.
/// Single source of truth shared by the runtime state-name map in the Plane client and the
/// `build setup` provisioning command, so the two can never drift. A project that does not
/// carry every entry here will fail at runtime (missing labels hard-fail the plan/chain
/// phases; a missing state downgrades a transition to a warning) - `build setup` exists to
/// create whatever is absent so a freshly `build init`'d project meets criteria.
/// </summary>
public static class WorkspaceSchema
{
    /// <summary>A required workflow state paired with the Plane state-group it belongs to.</summary>
    public readonly record struct RequiredState(string Name, string Group, TicketState State);

    /// <summary>
    /// The seven workflow states in display order. <c>Group</c> is the Plane state-group enum
    /// (<c>backlog</c> | <c>unstarted</c> | <c>started</c> | <c>completed</c> | <c>cancelled</c>);
    /// Plane requires every state to belong to exactly one group.
    /// </summary>
    public static readonly IReadOnlyList<RequiredState> States = new[]
    {
        new RequiredState("Backlog",     "backlog",   TicketState.Backlog),
        new RequiredState("Planning",    "unstarted", TicketState.Planning),
        new RequiredState("Ready",       "unstarted", TicketState.Ready),
        new RequiredState("In Progress", "started",   TicketState.InProgress),
        new RequiredState("In Review",   "started",   TicketState.InReview),
        new RequiredState("Done",        "completed", TicketState.Done),
        new RequiredState("Cancelled",   "cancelled", TicketState.Cancelled),
    };

    /// <summary>
    /// The required workspace-standard labels. <c>risk:*</c> and <c>size:*</c> are applied by the
    /// plan phase and hard-fail the run when absent; <c>plan-ticket</c> is applied by scaffold; and
    /// <c>stub</c> / <c>delegated</c> complete the workflow vocabulary the ticket verbs expect.
    /// </summary>
    public static readonly IReadOnlyList<string> Labels = new[]
    {
        "risk:low", "risk:medium", "risk:high",
        "size:s", "size:m", "size:l",
        "plan-ticket", "stub", "delegated",
    };
}
