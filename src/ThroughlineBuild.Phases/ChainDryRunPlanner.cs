using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public sealed record DryRunItem(
    Ticket Ticket,
    int Depth,
    bool HasLiveChildren,
    string IntegrationBranch,
    string BaseBranch);

public sealed record DryRunPlan(
    Ticket Root,
    IReadOnlyList<DryRunItem> PostOrder,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds and prints deterministic chain previews without executing workflow phases.
/// </summary>
public sealed class ChainDryRunPlanner
{
    private readonly ITicketing _ticketing;
    private readonly TextWriter _writer;

    public ChainDryRunPlanner(ITicketing ticketing, TextWriter writer)
    {
        _ticketing = ticketing;
        _writer = writer;
    }

    public async Task<DryRunPlan> BuildAsync(
        Ticket root,
        string targetBranch,
        int maxDepth,
        CancellationToken ct)
    {
        var items = new List<DryRunItem>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        await VisitAsync(
            root,
            depth: 0,
            parentIntegrationBranch: targetBranch,
            maxDepth,
            visited,
            items,
            warnings,
            ct).ConfigureAwait(false);

        return new DryRunPlan(root, items.AsReadOnly(), warnings.AsReadOnly());
    }

    private async Task VisitAsync(
        Ticket ticket,
        int depth,
        string parentIntegrationBranch,
        int maxDepth,
        HashSet<string> visited,
        List<DryRunItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!visited.Add(ticket.Uuid))
        {
            warnings.Add($"cycle detected at {ticket.Id}; subtree omitted");
            return;
        }

        var children = await _ticketing
            .QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct)
            .ConfigureAwait(false);
        var eligible = children
            .Where(child => child.State != TicketState.Done && child.State != TicketState.Cancelled)
            .Where(child => !string.Equals(child.Uuid, ticket.Uuid, StringComparison.Ordinal))
            .OrderBy(child => TicketIdOrdering.Number(child.Id))
            .ThenBy(child => child.Id, StringComparer.Ordinal)
            .ToList();

        var integrationBranch = eligible.Count > 0
            ? ChainIntegrationBranch(ticket)
            : PhaseWorktreeLayout.BranchName(ticket.Id);

        if (eligible.Count > 0)
        {
            if (depth >= maxDepth)
            {
                warnings.Add($"depth cap {maxDepth} reached at {ticket.Id}; subtree omitted");
            }
            else
            {
                var graph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
                var levels = TopologicalSorter.ComputeLevels(graph);
                foreach (var childId in levels.SelectMany(level => level))
                {
                    var child = eligible.First(
                        candidate => string.Equals(candidate.Id, childId, StringComparison.Ordinal));
                    await VisitAsync(
                        child,
                        depth + 1,
                        integrationBranch,
                        maxDepth,
                        visited,
                        items,
                        warnings,
                        ct).ConfigureAwait(false);
                }
            }
        }

        items.Add(new DryRunItem(
            ticket,
            depth,
            eligible.Count > 0,
            integrationBranch,
            parentIntegrationBranch));
        visited.Remove(ticket.Uuid);
    }

    public void Print(DryRunPlan plan, int maxDepth)
    {
        _writer.WriteLine($"[{plan.Root.Id}] dry-run chain plan (max depth {maxDepth}):");
        _writer.WriteLine("post-order schedule:");
        for (int i = 0; i < plan.PostOrder.Count; i++)
        {
            var item = plan.PostOrder[i];
            var action = item.HasLiveChildren
                ? $"roll up internal node on {item.IntegrationBranch}"
                : "run plan/implement/review/ship";
            _writer.WriteLine(
                $"  {i + 1}. {new string(' ', item.Depth * 2)}{item.Ticket.Id} - {action}");
        }

        _writer.WriteLine("branch topology:");
        foreach (var item in plan.PostOrder
                     .Where(item => item.HasLiveChildren)
                     .OrderBy(item => item.Depth)
                     .ThenBy(item => TicketIdOrdering.Number(item.Ticket.Id)))
        {
            _writer.WriteLine(
                $"  {item.IntegrationBranch} from {item.BaseBranch} integrates subtree for {item.Ticket.Id}");
        }
        foreach (var item in plan.PostOrder.Where(item => !item.HasLiveChildren))
        {
            _writer.WriteLine(
                $"  {PhaseWorktreeLayout.BranchName(item.Ticket.Id)} from {item.BaseBranch} before {item.Ticket.Id}");
        }

        if (plan.Warnings.Count > 0)
        {
            _writer.WriteLine("warnings:");
            foreach (var warning in plan.Warnings)
                _writer.WriteLine($"  {warning}");
        }
    }

    public void PrintDispatchOrder(
        string parentId,
        IReadOnlyList<IReadOnlyList<string>> levels)
    {
        _writer.WriteLine(
            $"[{parentId}] dispatch order ({levels.Count} level{(levels.Count == 1 ? "" : "s")}):");
        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var ticketList = string.Join(", ", level);
            var unorderedNote = level.Count > 1 ? " (unordered)" : "";
            _writer.WriteLine($"  level {i + 1}: {ticketList}{unorderedNote}");
        }
    }

    private async Task<TicketGraph> BuildSiblingGraphAsync(
        IReadOnlyList<Ticket> eligible,
        CancellationToken ct)
    {
        var graph = new TicketGraph();
        var eligibleIdSet = new HashSet<string>(
            eligible.Select(ticket => ticket.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in eligible)
            graph.AddNode(ticket.Id);

        var relationsByTicket = await Task.WhenAll(eligible.Select(async ticket => new
        {
            TicketId = ticket.Id,
            Relations = await _ticketing.GetRelationsAsync(ticket.Id, ct).ConfigureAwait(false)
        })).ConfigureAwait(false);

        foreach (var ticketRelations in relationsByTicket)
        {
            foreach (var relation in ticketRelations.Relations)
            {
                if (relation.Kind == "blocked_by" && eligibleIdSet.Contains(relation.TargetId))
                    graph.AddEdge(relation.TargetId, ticketRelations.TicketId);
            }
        }

        return graph;
    }

    private static string ChainIntegrationBranch(Ticket ticket) =>
        $"chain/{SlugBuilder.BuildTicketSlug(ticket.Id)}";
}
