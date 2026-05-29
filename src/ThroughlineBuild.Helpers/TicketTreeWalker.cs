using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Helpers;

public sealed class TicketTreeWalker
{
    private readonly ITicketing _ticketing;

    public TicketTreeWalker(ITicketing ticketing)
    {
        _ticketing = ticketing;
    }

    public async Task<TicketNode> WalkAsync(string ticketId, int depth = 2, CancellationToken ct = default)
    {
        var rootTicket = await _ticketing.GetAsync(ticketId, ct);
        var rootNode = new MutableNode(rootTicket);
        var queue = new Queue<(MutableNode Node, int Level)>();
        queue.Enqueue((rootNode, 0));

        while (queue.Count > 0)
        {
            var (node, level) = queue.Dequeue();
            if (level < depth)
            {
                var children = await _ticketing.QueryAsync(new TicketQuery(ParentId: node.Ticket.Uuid), ct);
                foreach (var child in children)
                {
                    var childNode = new MutableNode(child);
                    node.Children.Add(childNode);
                    queue.Enqueue((childNode, level + 1));
                }
            }
        }

        return rootNode.ToImmutable();
    }

    private sealed class MutableNode
    {
        public Ticket Ticket { get; }
        public List<MutableNode> Children { get; } = new();

        public MutableNode(Ticket ticket)
        {
            Ticket = ticket;
        }

        public TicketNode ToImmutable() =>
            new(Ticket, Children.Select(c => c.ToImmutable()).ToList());
    }
}
