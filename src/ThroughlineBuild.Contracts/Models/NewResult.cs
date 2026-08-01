namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// Result returned by NewPhase.RunAsync after creating a ticket.
/// </summary>
/// <param name="Id">Human-readable identifier of the created ticket, e.g. "TLB-42".</param>
/// <param name="Uuid">Plane work item UUID.</param>
/// <param name="ValidationWarnings">Non-fatal validation issues encountered during parsing and validation.</param>
/// <param name="Ticket">Authoritative read-back representation when the create path requested one.</param>
public record NewResult(
    string Id,
    string Uuid,
    IReadOnlyList<string> ValidationWarnings,
    Ticket? Ticket = null);
