namespace ThroughlineBuild.Contracts.Models;

/// <summary>A ticket-owned binary asset exposed by the ticketing backend.</summary>
public sealed record TicketAttachment(
    string Id,
    string Source,
    string? Name,
    string? ContentType,
    long? SizeBytes);

/// <summary>Downloaded attachment bytes paired with their normalized metadata.</summary>
public sealed record TicketAttachmentDownload(
    TicketAttachment Attachment,
    byte[] Content);
