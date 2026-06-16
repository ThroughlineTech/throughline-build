using System.Text.Json;
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

    /// <summary>Project a domain <see cref="Ticket"/> onto its wire shape.</summary>
    public static TicketView ToView(Ticket ticket) => new(
        Id: ticket.Id,
        Uuid: ticket.Uuid,
        Title: ticket.Title,
        Type: ticket.Type,
        State: ticket.State,
        Size: ticket.Size,
        Risk: ticket.Risk,
        DescriptionHtml: ticket.DescriptionHtml,
        ParentId: ticket.ParentId,
        Labels: ticket.Labels,
        Relations: ticket.Relations.Select(r => new RelationView(r.Kind, r.TargetId)).ToList());
}
