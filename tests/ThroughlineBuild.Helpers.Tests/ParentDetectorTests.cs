using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class ParentDetectorTests
{
    [Fact]
    public async Task HasChildrenAsync_LeafTicket_ReturnsFalse()
    {
        var fake = new FakeTicketing();
        var leaf = MakeTicket("TLB-10", "uuid-leaf");
        fake.AddTicket(leaf);

        var result = await ParentDetector.HasChildrenAsync(fake, leaf.Uuid, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task HasChildrenAsync_TicketWithOneChild_ReturnsTrue()
    {
        var fake = new FakeTicketing();
        var parent = MakeTicket("TLB-20", "uuid-parent");
        var child = MakeTicket("TLB-21", "uuid-child");
        fake.AddTicket(parent);
        fake.AddChildren("uuid-parent", child);

        var result = await ParentDetector.HasChildrenAsync(fake, parent.Uuid, CancellationToken.None);

        Assert.True(result);
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
