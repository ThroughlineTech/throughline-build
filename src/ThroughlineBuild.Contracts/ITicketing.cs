namespace ThroughlineBuild.Contracts;

using ThroughlineBuild.Contracts.Models;

/// <summary>
/// Interface for ticketing system operations.
/// Implementations may support a subset of operations; use BackendCapabilities to check availability.
/// </summary>
public interface ITicketing
{
    /// <summary>
    /// Describes the optional capabilities supported by this ticketing backend.
    /// </summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// Fetch a single ticket by ID.
    /// </summary>
    Task<Ticket> GetAsync(string id, CancellationToken ct);

    /// <summary>
    /// Fetch multiple tickets by ID in a single batch operation.
    /// </summary>
    Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct);

    /// <summary>
    /// Transition a ticket to a new state.
    /// </summary>
    Task TransitionAsync(string id, TicketState newState, CancellationToken ct);

    /// <summary>
    /// Append HTML content to a ticket's description.
    /// </summary>
    Task AppendDescriptionAsync(string id, string html, CancellationToken ct);

    /// <summary>
    /// Create a comment on a ticket. Returns the ID of the created comment.
    /// </summary>
    Task<string> CreateCommentAsync(string id, string html, CancellationToken ct);

    /// <summary>
    /// Apply one or more labels to a ticket.
    /// </summary>
    Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct);

    /// <summary>
    /// Fetch all relations for a ticket.
    /// </summary>
    Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct);

    /// <summary>
    /// Compute client-side rollup and transition the parent ticket if warranted.
    /// Never throws; failures are surfaced in RollupResult.FailureReason.
    /// </summary>
    Task<RollupResult> RollupParentAsync(string id, CancellationToken ct);

    /// <summary>
    /// Fetch all comments on a ticket. Returns empty list on 404 or empty result.
    /// </summary>
    Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct);
}

/// <summary>
/// A single comment on a ticket.
/// </summary>
public record TicketComment(string Id, string Body, DateTimeOffset CreatedAt);

/// <summary>
/// Describes optional capabilities supported by a ticketing backend.
/// </summary>
public record BackendCapabilities(
    bool TypedRelations,
    bool TypedLabels,
    bool RichHtmlComments,
    bool Attachments);

/// <summary>
/// Result returned by RollupParentAsync.
/// </summary>
public record RollupResult(bool ParentTransitioned, string? NewParentState, string? FailureReason);
