namespace ThroughlineBuild.Contracts.Models;

public record TicketNode(Ticket Root, IReadOnlyList<TicketNode> Children);
