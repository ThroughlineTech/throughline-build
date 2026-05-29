using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

public class ITicketingStubTests
{
    /// <summary>
    /// Simple in-memory stub implementation for testing the interface.
    /// </summary>
    private class TicketingStub : ITicketing
    {
        private readonly Dictionary<string, Ticket> _tickets = new();
        private readonly Dictionary<string, List<string>> _comments = new();
        private readonly Dictionary<string, List<Relation>> _relations = new();

        public BackendCapabilities Capabilities { get; } = new(
            TypedRelations: true,
            TypedLabels: true,
            RichHtmlComments: true,
            Attachments: true);

        public TicketingStub()
        {
            // Initialize with a test ticket
            var testTicket = new Ticket(
                Id: "TLB-1",
                Title: "Test ticket",
                Type: "feature",
                State: TicketState.Ready,
                Size: Size.M,
                Risk: Risk.Low,
                DescriptionHtml: "<p>Test description</p>",
                Relations: Array.Empty<Relation>(),
                Labels: Array.Empty<string>(),
                ParentId: null);
            _tickets["TLB-1"] = testTicket;
            _comments["TLB-1"] = new List<string>();
            _relations["TLB-1"] = new List<Relation>();
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            if (_tickets.TryGetValue(id, out var ticket))
                return Task.FromResult(ticket);
            throw new KeyNotFoundException($"Ticket {id} not found");
        }

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct)
        {
            var results = ids
                .Where(id => _tickets.ContainsKey(id))
                .Select(id => _tickets[id])
                .ToList();
            return Task.FromResult((IReadOnlyList<Ticket>)results);
        }

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            if (!_tickets.TryGetValue(id, out var ticket))
                throw new KeyNotFoundException($"Ticket {id} not found");

            _tickets[id] = ticket with { State = newState };
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            if (!_tickets.TryGetValue(id, out var ticket))
                throw new KeyNotFoundException($"Ticket {id} not found");

            var updated = ticket with { DescriptionHtml = ticket.DescriptionHtml + html };
            _tickets[id] = updated;
            return Task.CompletedTask;
        }

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            if (!_tickets.TryGetValue(id, out _))
                throw new KeyNotFoundException($"Ticket {id} not found");

            var commentId = $"comment-{Guid.NewGuid():N}";
            if (!_comments.ContainsKey(id))
                _comments[id] = new List<string>();
            _comments[id].Add(html);
            return Task.FromResult(commentId);
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
        {
            if (!_tickets.TryGetValue(id, out var ticket))
                throw new KeyNotFoundException($"Ticket {id} not found");

            _tickets[id] = ticket with { Labels = labels.ToList() };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
        {
            if (!_relations.TryGetValue(id, out var relations))
                return Task.FromResult((IReadOnlyList<Relation>)Array.Empty<Relation>());
            return Task.FromResult((IReadOnlyList<Relation>)relations);
        }

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct)
            => Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task ITicketing_GetAsync_ReturnsTicket()
    {
        var stub = new TicketingStub();
        var ticket = await stub.GetAsync("TLB-1", CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal("TLB-1", ticket.Id);
        Assert.Equal("Test ticket", ticket.Title);
    }

    [Fact]
    public async Task ITicketing_GetAsync_ThrowsWhenNotFound()
    {
        var stub = new TicketingStub();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => stub.GetAsync("TLB-999", CancellationToken.None));
    }

    [Fact]
    public async Task ITicketing_GetBatchAsync_ReturnsBatch()
    {
        var stub = new TicketingStub();
        var tickets = await stub.GetBatchAsync(new[] { "TLB-1" }, CancellationToken.None);

        Assert.Single(tickets);
        Assert.Equal("TLB-1", tickets[0].Id);
    }

    [Fact]
    public async Task ITicketing_TransitionAsync_ChangesState()
    {
        var stub = new TicketingStub();
        await stub.TransitionAsync("TLB-1", TicketState.Done, CancellationToken.None);

        var ticket = await stub.GetAsync("TLB-1", CancellationToken.None);
        Assert.Equal(TicketState.Done, ticket.State);
    }

    [Fact]
    public async Task ITicketing_AppendDescriptionAsync_AppendsHtml()
    {
        var stub = new TicketingStub();
        await stub.AppendDescriptionAsync("TLB-1", "<p>Additional</p>", CancellationToken.None);

        var ticket = await stub.GetAsync("TLB-1", CancellationToken.None);
        Assert.Contains("Additional", ticket.DescriptionHtml);
    }

    [Fact]
    public async Task ITicketing_CreateCommentAsync_ReturnsCommentId()
    {
        var stub = new TicketingStub();
        var commentId = await stub.CreateCommentAsync("TLB-1", "<p>comment</p>", CancellationToken.None);

        Assert.NotNull(commentId);
        Assert.NotEmpty(commentId);
    }

    [Fact]
    public async Task ITicketing_ApplyLabelsAsync_AddsLabels()
    {
        var stub = new TicketingStub();
        await stub.ApplyLabelsAsync("TLB-1", new[] { "bug", "urgent" }, CancellationToken.None);

        var ticket = await stub.GetAsync("TLB-1", CancellationToken.None);
        Assert.Contains("bug", ticket.Labels);
        Assert.Contains("urgent", ticket.Labels);
    }

    [Fact]
    public async Task ITicketing_ApplyLabelsAsync_ReplacesLabelsOnSecondCall()
    {
        var stub = new TicketingStub();
        await stub.ApplyLabelsAsync("TLB-1", new[] { "bug", "urgent" }, CancellationToken.None);
        await stub.ApplyLabelsAsync("TLB-1", new[] { "feature" }, CancellationToken.None);

        var ticket = await stub.GetAsync("TLB-1", CancellationToken.None);
        Assert.Single(ticket.Labels);
        Assert.Contains("feature", ticket.Labels);
        Assert.DoesNotContain("bug", ticket.Labels);
        Assert.DoesNotContain("urgent", ticket.Labels);
    }

    [Fact]
    public async Task ITicketing_GetRelationsAsync_ReturnsEmpty()
    {
        var stub = new TicketingStub();
        var relations = await stub.GetRelationsAsync("TLB-1", CancellationToken.None);

        Assert.Empty(relations);
    }

    [Fact]
    public void BackendCapabilities_Record_Compiles()
    {
        var caps = new BackendCapabilities(
            TypedRelations: true,
            TypedLabels: true,
            RichHtmlComments: true,
            Attachments: true);

        Assert.True(caps.TypedRelations);
        Assert.True(caps.TypedLabels);
        Assert.True(caps.RichHtmlComments);
        Assert.True(caps.Attachments);
    }
}
