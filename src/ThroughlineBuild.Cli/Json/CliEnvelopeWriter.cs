using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli.Json;

/// <summary>
/// Serializes <see cref="CliErrorCodes">--json</see> envelopes to a writer (stdout in practice).
/// The single place that stamps the current schema version, so verbs never construct an
/// envelope by hand. Each call writes exactly one envelope followed by a newline.
/// </summary>
public static class CliEnvelopeWriter
{
    /// <summary>Current envelope schema version. Bump only on an additive change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Write a uniform failure envelope: <c>{schemaVersion, ok:false, error:{code,message}}</c>.</summary>
    public static void WriteError(TextWriter output, string code, string message)
    {
        var envelope = new ErrorEnvelope(SchemaVersion, Ok: false, new CliError(code, message));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.ErrorEnvelope));
    }

    /// <summary>Write a success envelope wrapping a single ticket.</summary>
    public static void WriteTicket(TextWriter output, TicketView ticket)
    {
        var envelope = new TicketEnvelope(SchemaVersion, Ok: true, ticket);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.TicketEnvelope));
    }

    /// <summary>Write a success envelope describing a newly created ticket.</summary>
    public static void WriteNewTicket(TextWriter output, NewTicketView created)
    {
        var envelope = new NewTicketEnvelope(SchemaVersion, Ok: true, created);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.NewTicketEnvelope));
    }

    /// <summary>Write a success envelope wrapping a list of ticket rows.</summary>
    public static void WriteList(TextWriter output, IReadOnlyList<Ticket> tickets)
    {
        var rows = tickets
            .Select(t => new ListTicketView(t.Id, t.Title, t.State, t.Type, t.ParentId))
            .ToList();
        var envelope = new ListEnvelope(SchemaVersion, Ok: true, rows);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.ListEnvelope));
    }

    /// <summary>
    /// Write a success envelope wrapping a ticket's comments. Bodies are rendered from Plane's
    /// stored HTML to plain text so the envelope is readable (agents and humans), not markup.
    /// </summary>
    public static void WriteComments(TextWriter output, IReadOnlyList<TicketComment> comments)
    {
        var rows = comments
            .Select(c => new CommentView(c.Id, HtmlToText.Render(c.Body), c.CreatedAt))
            .ToList();
        var envelope = new CommentsEnvelope(SchemaVersion, Ok: true, rows);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.CommentsEnvelope));
    }

    /// <summary>Write a success envelope describing a newly created comment.</summary>
    public static void WriteCommentCreated(TextWriter output, string commentId)
    {
        var envelope = new CommentCreatedEnvelope(SchemaVersion, Ok: true, new CommentCreatedView(commentId));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.CommentCreatedEnvelope));
    }

    /// <summary>Write a success envelope describing a state transition.</summary>
    public static void WriteTransition(TextWriter output, string ticketId, TicketState state)
    {
        var envelope = new TransitionEnvelope(SchemaVersion, Ok: true, new TransitionView(ticketId, state));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.TransitionEnvelope));
    }

    /// <summary>Write a success acknowledgement for a lifecycle verb (close, defer, reopen, amend).</summary>
    public static void WriteAck(TextWriter output, string ticketId, string action)
    {
        var envelope = new AckEnvelope(SchemaVersion, Ok: true, new AckView(ticketId, action));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.AckEnvelope));
    }

    /// <summary>Project a domain <see cref="Ticket"/> onto its wire shape.</summary>
    public static TicketView ToView(Ticket ticket) => new(
        Id: ticket.Id,
        Uuid: ticket.Uuid,
        Title: ticket.Title,
        Type: ticket.Type,
        State: ticket.State,
        Size: ticket.Size,
        Risk: ticket.Risk,
        Description: HtmlToText.Render(ticket.DescriptionHtml),
        DescriptionHtml: ticket.DescriptionHtml,
        ParentId: ticket.ParentId,
        Labels: ticket.Labels,
        Relations: ticket.Relations.Select(r => new RelationView(r.Kind, r.TargetId)).ToList());
}
