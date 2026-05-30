namespace ThroughlineBuild.Phases;

// Input graph for topological sorting: nodes (ticket IDs) and directed edges (blocker -> blocked).
public sealed class TicketGraph
{
    private readonly List<string> _nodes = new();
    private readonly List<(string Blocker, string Blocked)> _edges = new();

    public void AddNode(string id) => _nodes.Add(id);
    public void AddEdge(string blocker, string blocked) => _edges.Add((blocker, blocked));
    public IReadOnlyList<string> Nodes => _nodes;
    public IReadOnlyList<(string Blocker, string Blocked)> Edges => _edges;
}

public static class TopologicalSorter
{
    // Kahn's BFS - throws InvalidOperationException on cycle.
    // Returns levels where same-level nodes are concurrency-eligible.
    // Preserves input order within each level as tiebreaker.
    public static IReadOnlyList<IReadOnlyList<string>> ComputeLevels(TicketGraph graph)
    {
        var nodes = graph.Nodes;
        var edges = graph.Edges;

        // Build adjacency: blocker -> list of blocked
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // In-degree count per node
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        // Initialise all known nodes
        foreach (var n in nodes)
        {
            if (!successors.ContainsKey(n))
                successors[n] = new List<string>();
            if (!inDegree.ContainsKey(n))
                inDegree[n] = 0;
        }

        foreach (var (blocker, blocked) in edges)
        {
            if (!successors.ContainsKey(blocker))
                successors[blocker] = new List<string>();
            if (!inDegree.ContainsKey(blocker))
                inDegree[blocker] = 0;
            if (!successors.ContainsKey(blocked))
                successors[blocked] = new List<string>();
            if (!inDegree.ContainsKey(blocked))
                inDegree[blocked] = 0;

            successors[blocker].Add(blocked);
            inDegree[blocked]++;
        }

        // Preserve input order within each level: build an order-index map
        var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
            orderIndex[nodes[i]] = i;

        var levels = new List<IReadOnlyList<string>>();
        int processed = 0;
        var allNodes = inDegree.Keys.ToList();

        while (true)
        {
            // Collect nodes with in-degree 0, preserving input order
            var ready = allNodes
                .Where(n => inDegree[n] == 0)
                .OrderBy(n => orderIndex.TryGetValue(n, out var idx) ? idx : int.MaxValue)
                .ToList();

            if (ready.Count == 0)
                break;

            levels.Add(ready.AsReadOnly());
            processed += ready.Count;

            foreach (var n in ready)
            {
                // Mark as removed from graph
                inDegree[n] = -1;
                foreach (var succ in successors[n])
                    inDegree[succ]--;
            }
        }

        // If not all nodes were processed, there is a cycle
        var unprocessed = allNodes.Where(n => inDegree[n] >= 0).ToList();
        if (unprocessed.Count > 0)
            throw new InvalidOperationException(
                $"Cycle detected in ticket graph involving: {string.Join(", ", unprocessed)}");

        return levels.AsReadOnly();
    }
}
