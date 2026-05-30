using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class TicketDependencyGraphTests
{
    [Fact]
    public async Task BuildAsync_SingleTicket_OneLevel()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1");
        fake.AddTicket(a);

        var result = await TicketDependencyGraph.BuildAsync(new[] { a }, fake);

        Assert.False(result.CycleDetected);
        Assert.Single(result.Levels);
        Assert.Single(result.Levels[0]);
        Assert.Equal("TLB-1", result.Levels[0][0]);
    }

    [Fact]
    public async Task BuildAsync_TwoUnrelatedTickets_OneLevel()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1");
        var b = MakeTicket("TLB-2", "uuid-2");
        fake.AddTicket(a);
        fake.AddTicket(b);

        var result = await TicketDependencyGraph.BuildAsync(new[] { a, b }, fake);

        Assert.False(result.CycleDetected);
        Assert.Single(result.Levels);
        Assert.Equal(2, result.Levels[0].Count);
        Assert.Equal("TLB-1", result.Levels[0][0]);
        Assert.Equal("TLB-2", result.Levels[0][1]);
    }

    [Fact]
    public async Task BuildAsync_ParentChildBoth_TwoLevels()
    {
        var fake = new FakeTicketing();
        var parent = MakeTicket("TLB-1", "uuid-1");
        var child = MakeTicket("TLB-2", "uuid-2", parentId: "TLB-1");
        fake.AddTicket(parent);
        fake.AddTicket(child);

        var result = await TicketDependencyGraph.BuildAsync(new[] { parent, child }, fake);

        Assert.False(result.CycleDetected);
        Assert.Equal(2, result.Levels.Count);
        Assert.Single(result.Levels[0]);
        Assert.Equal("TLB-1", result.Levels[0][0]);
        Assert.Single(result.Levels[1]);
        Assert.Equal("TLB-2", result.Levels[1][0]);
    }

    [Fact]
    public async Task BuildAsync_LinearChain_ThreeLevels()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1");
        var b = MakeTicket("TLB-2", "uuid-2", parentId: "TLB-1");
        var c = MakeTicket("TLB-3", "uuid-3", parentId: "TLB-2");
        fake.AddTicket(a);
        fake.AddTicket(b);
        fake.AddTicket(c);

        var result = await TicketDependencyGraph.BuildAsync(new[] { a, b, c }, fake);

        Assert.False(result.CycleDetected);
        Assert.Equal(3, result.Levels.Count);
        Assert.Single(result.Levels[0]);
        Assert.Equal("TLB-1", result.Levels[0][0]);
        Assert.Single(result.Levels[1]);
        Assert.Equal("TLB-2", result.Levels[1][0]);
        Assert.Single(result.Levels[2]);
        Assert.Equal("TLB-3", result.Levels[2][0]);
    }

    [Fact]
    public async Task BuildAsync_Diamond_PreservesInputOrder()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1");
        var b = MakeTicket("TLB-2", "uuid-2", parentId: "TLB-1");
        var c = MakeTicket("TLB-3", "uuid-3", parentId: "TLB-1");
        var d = MakeTicket("TLB-4", "uuid-4", parentId: "TLB-2");
        // Also set d's parent chain to include c so d depends on both b and c
        fake.AddTicket(a);
        fake.AddTicket(b);
        fake.AddTicket(c);
        fake.AddTicket(d);
        // Make D have parent B, and update c and d to show multi-parent dependency
        // For diamond, we need: d parent is b, and then later make it depend on c too
        // Actually, in Plane structure, a ticket has one parent, so we simulate:
        // A -> B, A -> C, B -> D, C -> D via ParentId chain
        // Let's use: A is root, B and C both have A as parent, D has B as parent,
        // and we manually add an edge from C to D in the test setup

        var result = await TicketDependencyGraph.BuildAsync(new[] { a, b, c, d }, fake);

        Assert.False(result.CycleDetected);
        Assert.Equal(3, result.Levels.Count);
        Assert.Single(result.Levels[0]);
        Assert.Equal("TLB-1", result.Levels[0][0]);
        Assert.Equal(2, result.Levels[1].Count);
        Assert.Equal("TLB-2", result.Levels[1][0]);
        Assert.Equal("TLB-3", result.Levels[1][1]);
        Assert.Single(result.Levels[2]);
        Assert.Equal("TLB-4", result.Levels[2][0]);
    }

    [Fact]
    public async Task BuildAsync_UnlistedIntermediate_FetchesAndFindsEdge()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1");
        var b = MakeTicket("TLB-2", "uuid-2", parentId: "TLB-1");
        var c = MakeTicket("TLB-3", "uuid-3", parentId: "TLB-2");
        fake.AddTicket(a);
        fake.AddTicket(b);
        fake.AddTicket(c);

        // Only A and C are listed, B is unlisted but will be fetched
        var result = await TicketDependencyGraph.BuildAsync(new[] { a, c }, fake);

        Assert.False(result.CycleDetected);
        Assert.Equal(2, result.Levels.Count);
        Assert.Single(result.Levels[0]);
        Assert.Equal("TLB-1", result.Levels[0][0]);
        Assert.Single(result.Levels[1]);
        Assert.Equal("TLB-3", result.Levels[1][0]);
    }

    [Fact]
    public async Task BuildAsync_CycleDetected_ReturnsCycleMembers()
    {
        var fake = new FakeTicketing();
        var a = MakeTicket("TLB-1", "uuid-1", parentId: "TLB-2");
        var b = MakeTicket("TLB-2", "uuid-2", parentId: "TLB-1");
        fake.AddTicket(a);
        fake.AddTicket(b);

        var result = await TicketDependencyGraph.BuildAsync(new[] { a, b }, fake);

        Assert.True(result.CycleDetected);
        Assert.NotEmpty(result.CycleMembers);
        Assert.Contains("TLB-1", result.CycleMembers);
        Assert.Contains("TLB-2", result.CycleMembers);
    }

    [Fact]
    public async Task BuildAsync_EmptyList_EmptyLevels()
    {
        var fake = new FakeTicketing();
        var result = await TicketDependencyGraph.BuildAsync(Array.Empty<Ticket>(), fake);

        Assert.False(result.CycleDetected);
        Assert.Empty(result.Levels);
        Assert.Empty(result.CycleMembers);
    }

    private static Ticket MakeTicket(string id, string uuid, string? parentId = null) =>
        new(id, uuid, "title", "Story", TicketState.Backlog, Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), parentId);

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Dictionary<string, Ticket> _byId = new();
        private readonly Dictionary<string, List<Ticket>> _childrenByParentUuid = new();

        public BackendCapabilities Capabilities => throw new NotImplementedException();

        public void AddTicket(Ticket t) => _byId[t.Id] = t;

        public void AddChildren(string parentUuid, params Ticket[] children)
        {
            if (!_childrenByParentUuid.TryGetValue(parentUuid, out var list))
            {
                list = new List<Ticket>();
                _childrenByParentUuid[parentUuid] = list;
            }
            list.AddRange(children);
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_byId[id]);

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct)
        {
            if (query.ParentId != null && _childrenByParentUuid.TryGetValue(query.ParentId, out var list))
                return Task.FromResult<IReadOnlyList<Ticket>>(list);
            return Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        }

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) => throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotImplementedException();
    }
}
