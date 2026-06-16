using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

// Covers NewPhase.RunFromStructuredAsync, the deterministic create path behind build new - --json.
// The key regression guard: the parent is resolved BEFORE the ticket is created, so a bad parent
// id fails fast without leaving an orphan ticket behind.
public class NewPhaseStructuredTests
{
    private static BuildOptions MakeOptions() => new(
        SessionId: "session-1",
        WorkerName: "deterministic",
        WorkerTimeout: TimeSpan.FromMinutes(1));

    [Fact]
    public async Task NoParent_RendersMarkdownToHtml_AndCreatesOnce()
    {
        var ticketing = new RecordingTicketing();
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var result = await phase.RunFromStructuredAsync(
            title: "Add a thing",
            type: "feature",
            descriptionMarkdown: "A body with **bold** and `code`.",
            acceptanceCriteriaMarkdown: "- it works",
            labels: new[] { "build" },
            parentId: null,
            ct: CancellationToken.None);

        Assert.Equal("TLB-100", result.Id);
        Assert.Equal("uuid-100", result.Uuid);

        var created = Assert.Single(ticketing.Created);
        Assert.Equal("Add a thing", created.Title);
        Assert.Equal("feature", created.Type);
        Assert.Equal(new[] { "build" }, created.Labels);
        // Markdown was rendered to HTML, and the acceptance criteria became its own section.
        Assert.Contains("<strong>bold</strong>", created.Html);
        Assert.Contains("<code>code</code>", created.Html);
        Assert.Contains("<h2>Acceptance criteria</h2>", created.Html);
        Assert.Contains("<li>it works</li>", created.Html);

        Assert.Equal(new[] { "create" }, ticketing.Calls);
    }

    [Fact]
    public async Task WithParent_ResolvesParentBeforeCreate_ThenReparents()
    {
        var ticketing = new RecordingTicketing();
        ticketing.Seed("TLB-10", "uuid-10");
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        await phase.RunFromStructuredAsync(
            "child", null, "body", null, null, parentId: "TLB-10", ct: CancellationToken.None);

        // Parent lookup happens first, then create, then reparent - the orphan-bug guard.
        Assert.Equal(new[] { "get:TLB-10", "create", "setparent:uuid-100->uuid-10" }, ticketing.Calls);
    }

    [Fact]
    public async Task BadParent_FailsFast_WithoutCreatingAnOrphan()
    {
        var ticketing = new RecordingTicketing(); // empty store: any parent lookup misses
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            phase.RunFromStructuredAsync("child", null, "body", null, null, "TLB-999", CancellationToken.None));

        Assert.Empty(ticketing.Created); // no ticket was created before the parent lookup failed
        Assert.Equal(new[] { "get:TLB-999" }, ticketing.Calls);
    }

    [Fact]
    public async Task EmptyTitle_Throws()
    {
        var phase = new NewPhase(new RecordingTicketing(), new NullEventSink(), MakeOptions());

        await Assert.ThrowsAsync<NewPhaseValidationException>(() =>
            phase.RunFromStructuredAsync("  ", null, "body", null, null, null, CancellationToken.None));
    }

    private sealed record CreatedTicket(string Title, string? Type, string Html, IReadOnlyList<string>? Labels);

    private sealed class RecordingTicketing : ITicketing
    {
        public List<string> Calls { get; } = new();
        public List<CreatedTicket> Created { get; } = new();
        private readonly Dictionary<string, string> _uuidById = new(StringComparer.Ordinal);

        public void Seed(string id, string uuid) => _uuidById[id] = uuid;

        public BackendCapabilities Capabilities => new(true, true, true, true);

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            Calls.Add($"get:{id}");
            if (!_uuidById.TryGetValue(id, out var uuid))
                throw new KeyNotFoundException($"Issue {id} not found");
            return Task.FromResult(new Ticket(id, uuid, "Parent", "", TicketState.Backlog,
                Size.M, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), null));
        }

        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct)
        {
            Calls.Add("create");
            Created.Add(new CreatedTicket(title, type, descriptionHtml, initialLabelNames));
            return Task.FromResult(new NewTicketResult("TLB-100", "uuid-100", new DateTime(2026, 1, 1)));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            Calls.Add($"setparent:{childUuid}->{parentUuid}");
            return Task.CompletedTask;
        }

        // Unused by RunFromStructuredAsync.
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class NullEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
