namespace ThroughlineBuild.Contracts.Models;

public record Ticket(
    string Id,
    string Uuid,
    string Title,
    string Type,
    TicketState State,
    Size Size,
    Risk Risk,
    string DescriptionHtml,
    IReadOnlyList<Relation> Relations,
    IReadOnlyList<string> Labels,
    string? ParentId);

public enum TicketState { Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled }
public enum Size { S, M, L }
public enum Risk { Low, Medium, High }
