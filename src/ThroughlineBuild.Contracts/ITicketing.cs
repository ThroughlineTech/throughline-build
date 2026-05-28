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
    /// Internal errors are caught and surfaced in RollupResult.FailureReason; this method never throws.
    /// </summary>
    Task<RollupResult> RollupParentAsync(string id, CancellationToken ct);

    /// <summary>
    /// Fetch all comments on a ticket. Returns empty list on 404 or empty result.
    /// </summary>
    Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct);

    /// <summary>
    /// Create a new ticket in the project and return its identifier and UUID.
    /// <para>
    /// The <paramref name="type"/> parameter maps to the backend's native issue-type field
    /// (e.g. Plane's structured "type" field). If the backend represents type as a label,
    /// implementations should prepend it to <paramref name="initialLabelNames"/>.
    /// </para>
    /// <para>
    /// The returned <see cref="NewTicketResult.Id"/> is the human-readable identifier
    /// (e.g. "TLB-42"), built as {ProjectIdentifier}-{sequence_id}.
    /// </para>
    /// <para>
    /// Initial state is whatever the backend assigns by default (typically Backlog).
    /// This method does NOT set state explicitly.
    /// </para>
    /// </summary>
    Task<NewTicketResult> CreateTicketAsync(
        string title,
        string? type,
        string descriptionHtml,
        IReadOnlyList<string>? initialLabelNames,
        CancellationToken ct);

    /// <summary>
    /// Set the parent of a ticket.
    /// Implemented via a PATCH to the issue endpoint with the parent UUID field.
    /// </summary>
    Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct);
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

/// <summary>
/// Result returned by CreateTicketAsync.
/// </summary>
/// <param name="Id">Human-readable identifier, e.g. "TLB-42".</param>
/// <param name="Uuid">Plane work item UUID.</param>
/// <param name="CreatedAt">Timestamp when the ticket was created (UTC).</param>
public record NewTicketResult(string Id, string Uuid, DateTime CreatedAt);
