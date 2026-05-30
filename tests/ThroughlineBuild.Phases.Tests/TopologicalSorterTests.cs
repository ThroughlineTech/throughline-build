using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class TopologicalSorterTests
{
    // Helper to build a graph with given nodes and edges (blocker, blocked).
    private static TicketGraph MakeGraph(
        IEnumerable<string> nodes,
        IEnumerable<(string Blocker, string Blocked)>? edges = null)
    {
        var g = new TicketGraph();
        foreach (var n in nodes)
            g.AddNode(n);
        if (edges is not null)
            foreach (var (blocker, blocked) in edges)
                g.AddEdge(blocker, blocked);
        return g;
    }

    [Fact]
    public void SingleNode_ReturnsOneLevel()
    {
        var g = MakeGraph(new[] { "A" });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Single(levels);
        Assert.Equal(new[] { "A" }, levels[0]);
    }

    [Fact]
    public void LinearChain_EachNodeInItsOwnLevel()
    {
        // A -> B -> C (A blocks B, B blocks C)
        var g = MakeGraph(
            new[] { "A", "B", "C" },
            new[] { ("A", "B"), ("B", "C") });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Equal(3, levels.Count);
        Assert.Equal(new[] { "A" }, levels[0]);
        Assert.Equal(new[] { "B" }, levels[1]);
        Assert.Equal(new[] { "C" }, levels[2]);
    }

    [Fact]
    public void Fork_BlockerThenTwoConcurrentNodes()
    {
        // A blocks B and C; B and C are independent
        var g = MakeGraph(
            new[] { "A", "B", "C" },
            new[] { ("A", "B"), ("A", "C") });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Equal(2, levels.Count);
        Assert.Equal(new[] { "A" }, levels[0]);
        // B and C must both be in level 1; order preserved from input
        Assert.Equal(2, levels[1].Count);
        Assert.Contains("B", levels[1]);
        Assert.Contains("C", levels[1]);
    }

    [Fact]
    public void Fork_InputOrderPreservedWithinLevel()
    {
        // A blocks both B and C. B added before C - B should appear first in level 1.
        var g = MakeGraph(
            new[] { "A", "B", "C" },
            new[] { ("A", "B"), ("A", "C") });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Equal("B", levels[1][0]);
        Assert.Equal("C", levels[1][1]);
    }

    [Fact]
    public void Diamond_CorrectThreeLevels()
    {
        // Diamond: A -> B, A -> C, B -> D, C -> D
        var g = MakeGraph(
            new[] { "A", "B", "C", "D" },
            new[] { ("A", "B"), ("A", "C"), ("B", "D"), ("C", "D") });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Equal(3, levels.Count);
        Assert.Equal(new[] { "A" }, levels[0]);
        Assert.Equal(2, levels[1].Count);
        Assert.Contains("B", levels[1]);
        Assert.Contains("C", levels[1]);
        Assert.Equal(new[] { "D" }, levels[2]);
    }

    [Fact]
    public void IndependentNodes_AllInFirstLevel()
    {
        var g = MakeGraph(new[] { "X", "Y", "Z" });
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Single(levels);
        Assert.Equal(3, levels[0].Count);
        Assert.Contains("X", levels[0]);
        Assert.Contains("Y", levels[0]);
        Assert.Contains("Z", levels[0]);
    }

    [Fact]
    public void EmptyGraph_ReturnsNoLevels()
    {
        var g = new TicketGraph();
        var levels = TopologicalSorter.ComputeLevels(g);
        Assert.Empty(levels);
    }

    [Fact]
    public void CycleDetected_ThrowsInvalidOperationException()
    {
        // A -> B -> A forms a cycle
        var g = MakeGraph(
            new[] { "A", "B" },
            new[] { ("A", "B"), ("B", "A") });
        var ex = Assert.Throws<InvalidOperationException>(() => TopologicalSorter.ComputeLevels(g));
        Assert.Contains("Cycle detected", ex.Message);
        Assert.Contains("A", ex.Message);
        Assert.Contains("B", ex.Message);
    }

    [Fact]
    public void SelfLoop_ThrowsInvalidOperationException()
    {
        var g = MakeGraph(new[] { "A" }, new[] { ("A", "A") });
        Assert.Throws<InvalidOperationException>(() => TopologicalSorter.ComputeLevels(g));
    }
}
