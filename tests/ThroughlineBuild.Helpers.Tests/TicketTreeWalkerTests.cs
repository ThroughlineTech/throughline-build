using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class TicketTreeWalkerTests
{
    [Fact]
    public async Task WalkAsync_DepthZero_ReturnsRootOnly()
    {
        var fake = new FakeTicketing();
        var root = MakeTicket("TLB-1", "uuid-1");
        fake.AddTicket(root);

        var walker = new TicketTreeWalker(fake);
        var result = await walker.WalkAsync("TLB-1", depth: 0);

        Assert.Equal("TLB-1", result.Root.Id);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task WalkAsync_DefaultDepth_ReturnsGrandchildren()
    {
        var fake = new FakeTicketing();
        var root = MakeTicket("TLB-1", "uuid-1");
        var child = MakeTicket("TLB-2", "uuid-2");
        var grandchild = MakeTicket("TLB-3", "uuid-3");

        fake.AddTicket(root);
        fake.AddChildren("uuid-1", child);
        fake.AddChildren("uuid-2", grandchild);

        var walker = new TicketTreeWalker(fake);
        var result = await walker.WalkAsync("TLB-1");

        Assert.Single(result.Children);
        Assert.Equal("TLB-2", result.Children[0].Root.Id);
        Assert.Single(result.Children[0].Children);
        Assert.Equal("TLB-3", result.Children[0].Children[0].Root.Id);
        Assert.Empty(result.Children[0].Children[0].Children);
    }

    [Fact]
    public async Task WalkAsync_DepthCap_ThirdLevelNotExpanded()
    {
        var fake = new FakeTicketing();
        var root = MakeTicket("TLB-1", "uuid-1");
        var child = MakeTicket("TLB-2", "uuid-2");
        var grandchild = MakeTicket("TLB-3", "uuid-3");
        var greatGrandchild = MakeTicket("TLB-4", "uuid-4");

        fake.AddTicket(root);
        fake.AddChildren("uuid-1", child);
        fake.AddChildren("uuid-2", grandchild);
        fake.AddChildren("uuid-3", greatGrandchild);

        var walker = new TicketTreeWalker(fake);
        var result = await walker.WalkAsync("TLB-1", depth: 2);

        var gcNode = result.Children[0].Children[0];
        Assert.Equal("TLB-3", gcNode.Root.Id);
        Assert.Empty(gcNode.Children);
    }

    private static Ticket MakeTicket(string id, string uuid) =>
        new(id, uuid, "title", "Story", TicketState.Backlog, Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), null);

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
    
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

    public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotImplementedException();
    }
}
