using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Helpers;

public static class TicketDependencyGraph
{
    public static async Task<TicketGraph> BuildAsync(
        IReadOnlyList<Ticket> listed,
        ITicketing ticketing,
        CancellationToken ct = default)
    {
        if (listed.Count == 0)
        {
            return new TicketGraph(new List<IReadOnlyList<string>>(), false, new List<string>());
        }

        // Build inList set for O(1) membership testing
        var inList = new HashSet<string>();
        var inputIndex = new Dictionary<string, int>();
        for (int i = 0; i < listed.Count; i++)
        {
            inList.Add(listed[i].Id);
            inputIndex[listed[i].Id] = i;
        }

        // Build edge list and in-degree map
        var inDegree = new Dictionary<string, int>();
        var edges = new Dictionary<string, List<string>>();

        // Initialize in-degree to 0 for all listed tickets
        foreach (var ticket in listed)
        {
            inDegree[ticket.Id] = 0;
            edges[ticket.Id] = new List<string>();
        }

        // Fetch cache to avoid re-fetching
        var fetchCache = new Dictionary<string, Ticket>();

        // Walk parent chain for each ticket
        foreach (var ticket in listed)
        {
            var seenAncestors = new HashSet<string>();
            var currentId = ticket.ParentId;

            while (currentId != null)
            {
                // Avoid duplicate edges
                if (seenAncestors.Contains(currentId))
                {
                    break;
                }
                seenAncestors.Add(currentId);

                if (inList.Contains(currentId))
                {
                    // Edge: currentId -> ticket.Id
                    if (!edges[currentId].Contains(ticket.Id))
                    {
                        edges[currentId].Add(ticket.Id);
                        inDegree[ticket.Id]++;
                    }
                    // Continue from that parent's parent
                    if (!fetchCache.TryGetValue(currentId, out var parentTicket))
                    {
                        parentTicket = await ticketing.GetAsync(currentId, ct);
                        fetchCache[currentId] = parentTicket;
                    }
                    currentId = parentTicket.ParentId;
                }
                else
                {
                    // Fetch parent not in list
                    if (!fetchCache.TryGetValue(currentId, out var unlistedParent))
                    {
                        unlistedParent = await ticketing.GetAsync(currentId, ct);
                        fetchCache[currentId] = unlistedParent;
                    }
                    currentId = unlistedParent.ParentId;
                }
            }
        }

        // Kahn's BFS topological sort
        var levels = new List<IReadOnlyList<string>>();
        var queue = new Queue<string>();

        // Seed with zero in-degree
        var zeroInDegree = inList
            .Where(id => inDegree[id] == 0)
            .OrderBy(id => inputIndex[id])
            .ToList();

        foreach (var id in zeroInDegree)
        {
            queue.Enqueue(id);
        }

        var processed = new HashSet<string>();

        while (queue.Count > 0)
        {
            var level = new List<string>();

            // Process all nodes at current level
            var levelSize = queue.Count;
            for (int i = 0; i < levelSize; i++)
            {
                var id = queue.Dequeue();
                level.Add(id);
                processed.Add(id);

                // Reduce in-degree for neighbors
                foreach (var neighbor in edges[id])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            // Sort level by input order
            level.Sort((a, b) => inputIndex[a].CompareTo(inputIndex[b]));
            levels.Add(level);
        }

        // Check for cycles
        if (processed.Count < inList.Count)
        {
            var cycleMembers = inList.Where(id => !processed.Contains(id)).ToList();
            return new TicketGraph(levels, true, cycleMembers);
        }

        return new TicketGraph(levels, false, new List<string>());
    }
}
