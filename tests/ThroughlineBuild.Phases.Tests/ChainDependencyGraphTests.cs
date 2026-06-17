using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins ChainDependencyGraph's id-normalization. A relation's TargetId is always the canonical
/// display id ("TLB-93") while the dispatched ids are whatever was typed on the CLI ("93" or
/// "TLB-93"). The graph must connect them on the numeric sequence, never raw string equality -
/// otherwise blocked_by ordering silently never forms an edge for a bare-number invocation.
/// </summary>
public class ChainDependencyGraphTests
{
    private static Dictionary<string, IReadOnlyList<Relation>> Rels(
        params (string ticket, string kind, string target)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<Relation>>();
        foreach (var (ticket, kind, target) in entries)
        {
            if (!map.TryGetValue(ticket, out var list))
                map[ticket] = list = new List<Relation>();
            ((List<Relation>)list).Add(new Relation(kind, target));
        }
        return map;
    }

    [Fact]
    public void AllTicketsBecomeNodes()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "93", "94" },
            new Dictionary<string, IReadOnlyList<Relation>>());

        Assert.Equal(new[] { "92", "93", "94" }, graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void BareNumberId_MatchesDisplayIdTarget()
    {
        // The reported bug: `build chain 92 93` with 92 blocked_by TLB-93 must form an edge.
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "93" },
            Rels(("92", "blocked_by", "TLB-93")));

        Assert.Contains(("93", "92"), graph.Edges); // TLB-93 (node "93") blocks 92
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void DisplayIdInvocation_AlsoMatches()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "TLB-92", "TLB-93" },
            Rels(("TLB-92", "blocked_by", "TLB-93")));

        Assert.Contains(("TLB-93", "TLB-92"), graph.Edges);
    }

    [Fact]
    public void MixedIdForms_MatchOnSequence()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "TLB-93" },
            Rels(("92", "blocked_by", "TLB-93")));

        Assert.Contains(("TLB-93", "92"), graph.Edges);
    }

    [Fact]
    public void NonBlockedByRelations_AreIgnored()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "93" },
            Rels(("92", "blocks", "TLB-93"), ("92", "relates_to", "TLB-93")));

        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void TargetOutsideDispatchSet_FormsNoEdge()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "93" },
            Rels(("92", "blocked_by", "TLB-200")));

        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void TicketWithNoRelationsEntry_DoesNotThrow()
    {
        var graph = ChainDependencyGraph.Build(
            new[] { "92", "93" },
            Rels(("92", "blocked_by", "TLB-93"))); // no entry for "93"

        Assert.Single(graph.Edges);
        Assert.Equal(2, graph.Nodes.Count);
    }
}
