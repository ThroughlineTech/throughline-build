namespace ThroughlineBuild.Phases;

using ThroughlineBuild.Contracts.Models;

// Builds the blocked_by dependency graph for a multi-ticket `build chain` run.
//
// Ticket ids arrive here in whatever form the user typed on the CLI ("92" or "TLB-92"),
// while a relation's TargetId always comes back as the canonical display id ("TLB-92").
// Matching the two with raw string equality silently never connects a bare-number
// invocation to its display-id targets, so both sides are normalized to the numeric
// sequence and every edge is keyed on the exact node string the graph already holds.
public static class ChainDependencyGraph
{
    public static TicketGraph Build(
        IReadOnlyList<string> ticketIds,
        IReadOnlyDictionary<string, IReadOnlyList<Relation>> relationsByTicketId)
    {
        var graph = new TicketGraph();
        foreach (var id in ticketIds)
            graph.AddNode(id);

        // Sequence number -> the exact id string used as that ticket's graph node.
        var nodeBySequence = new Dictionary<int, string>();
        foreach (var id in ticketIds)
            if (TryParseSequence(id, out var seq))
                nodeBySequence[seq] = id;

        foreach (var id in ticketIds)
        {
            if (!relationsByTicketId.TryGetValue(id, out var relations))
                continue;

            foreach (var rel in relations)
            {
                // `id` is blocked_by TargetId, so TargetId blocks id. Only connect targets that
                // are themselves part of this dispatch set.
                if (rel.Kind == "blocked_by"
                    && TryParseSequence(rel.TargetId, out var targetSeq)
                    && nodeBySequence.TryGetValue(targetSeq, out var targetNode))
                {
                    graph.AddEdge(targetNode, id);
                }
            }
        }

        return graph;
    }

    // Accepts "TLB-92" or bare "92"; yields the trailing integer sequence.
    private static bool TryParseSequence(string id, out int seq)
    {
        var dash = id.LastIndexOf('-');
        return int.TryParse(dash >= 0 ? id[(dash + 1)..] : id, out seq);
    }
}
