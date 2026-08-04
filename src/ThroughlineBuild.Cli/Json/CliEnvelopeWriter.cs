using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

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
            .Select(t => new ListTicketView(
                t.Id, t.Title, t.State, t.Type, t.Size, t.Risk, t.Labels, t.ParentId))
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

    /// <summary>Write the created structured evidence comment and its read-back proof.</summary>
    public static void WriteEvidence(TextWriter output, EvidenceView evidence)
    {
        var envelope = new EvidenceEnvelope(SchemaVersion, Ok: true, evidence);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.EvidenceEnvelope));
    }

    /// <summary>Write a success envelope describing a state transition.</summary>
    public static void WriteTransition(TextWriter output, string ticketId, TicketState state)
    {
        var envelope = new TransitionEnvelope(SchemaVersion, Ok: true, new TransitionView(ticketId, state));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.TransitionEnvelope));
    }

    /// <summary>Write an explicit relation list, including stable backend edge ids.</summary>
    public static void WriteRelations(TextWriter output, IReadOnlyList<Relation> relations)
    {
        var rows = relations.Select(r => new ManagedRelationView(
            r.Id ?? string.Empty, r.Kind, r.TargetId)).ToList();
        var envelope = new RelationsEnvelope(SchemaVersion, Ok: true, rows);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.RelationsEnvelope));
    }

    /// <summary>Write a relation create/remove acknowledgement.</summary>
    public static void WriteRelate(TextWriter output, RelateView result)
    {
        var envelope = new RelateEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.RelateEnvelope));
    }

    /// <summary>Write a success acknowledgement for a lifecycle verb (close, defer, reopen, amend).</summary>
    public static void WriteAck(TextWriter output, string ticketId, string action)
    {
        var envelope = new AckEnvelope(SchemaVersion, Ok: true, new AckView(ticketId, action));
        output.WriteLine(JsonSerializer.Serialize(envelope, CliJsonContext.Default.AckEnvelope));
    }

    public static void WriteWorktreeLease(TextWriter output, WorktreeLeaseManifest manifest)
    {
        var envelope = new WorktreeLeaseEnvelope(
            SchemaVersion, Ok: true, new WorktreeLeaseView(manifest.WorktreePath, manifest));
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.WorktreeLeaseEnvelope));
    }

    public static void WriteWorktreeList(TextWriter output, WorktreeLeaseListResult result)
    {
        var envelope = new WorktreeListEnvelope(
            SchemaVersion,
            Ok: true,
            new WorktreeListView(result.Leases, result.UnmanifestedDirectories));
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.WorktreeListEnvelope));
    }

    public static void WriteWorktreeTeardown(TextWriter output, WorktreeLeaseManifest manifest)
    {
        var envelope = new WorktreeTeardownEnvelope(
            SchemaVersion,
            Ok: true,
            new WorktreeTeardownView(manifest.WorktreePath, manifest.Branch));
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.WorktreeTeardownEnvelope));
    }

    public static void WriteCandidateStatus(TextWriter output, CandidateStatusView result)
    {
        var envelope = new CandidateStatusEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.CandidateStatusEnvelope));
    }

    public static void WriteGate(TextWriter output, GateView result)
    {
        var envelope = new GateEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.GateEnvelope));
    }

    public static void WriteProfilePrompt(TextWriter output, ProfilePromptView result)
    {
        var envelope = new ProfilePromptEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.ProfilePromptEnvelope));
    }

    public static void WriteProfileOperation(TextWriter output, ProfileOperationView result)
    {
        var envelope = new ProfileOperationEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.ProfileOperationEnvelope));
    }

    public static void WriteWaves(TextWriter output, WavePlan result)
    {
        var envelope = new WavesEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.WavesEnvelope));
    }

    public static void WriteSopDoctor(TextWriter output, SopDoctorView result)
    {
        var envelope = new SopDoctorEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.SopDoctorEnvelope));
    }

    public static void WriteSopList(TextWriter output, IReadOnlyList<SopListItemView> result)
    {
        var envelope = new SopListEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.SopListEnvelope));
    }

    public static void WriteSopBrief(TextWriter output, SopBriefView result)
    {
        var envelope = new SopBriefEnvelope(SchemaVersion, Ok: result.Ready, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.SopBriefEnvelope));
    }

    public static void WriteSopOperation(TextWriter output, SopOperationView result)
    {
        var envelope = new SopOperationEnvelope(SchemaVersion, Ok: result.Passed, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.SopOperationEnvelope));
    }

    public static void WriteWorkerBrief(TextWriter output, WorkerBriefView result)
    {
        var envelope = new WorkerBriefEnvelope(SchemaVersion, Ok: true, result);
        output.WriteLine(JsonSerializer.Serialize(
            envelope, CliJsonContext.Default.WorkerBriefEnvelope));
    }

    /// <summary>Project a domain <see cref="Ticket"/> onto its wire shape.</summary>
    public static TicketView ToView(
        Ticket ticket,
        IReadOnlyList<Ticket>? children = null) => new(
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
        Relations: ticket.Relations.Select(r => new RelationView(r.Kind, r.TargetId)).ToList(),
        Children: (children ?? Array.Empty<Ticket>())
            .Select(c => new TicketChildView(c.Id, c.Title, c.State))
            .ToList());
}
