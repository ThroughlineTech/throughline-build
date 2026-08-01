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
        Assert.NotNull(result.Ticket);
        Assert.Equal("Add a thing", result.Ticket!.Title);

        var created = Assert.Single(ticketing.Created);
        Assert.Equal("Add a thing", created.Title);
        Assert.Equal("feature", created.Type);
        Assert.Equal(new[] { "build" }, created.Labels);
        // Markdown was rendered to HTML, and the acceptance criteria became its own section.
        Assert.Contains("<strong>bold</strong>", created.Html);
        Assert.Contains("<code>code</code>", created.Html);
        Assert.Contains("<h2>Acceptance criteria</h2>", created.Html);
        Assert.Contains("<li>it works</li>", created.Html);

        Assert.Equal(new[] { "create", "get:TLB-100", "relations:TLB-100" }, ticketing.Calls);
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
        Assert.Equal(new[]
        {
            "get:TLB-10", "create", "setparent:uuid-100->uuid-10", "get:TLB-100", "relations:TLB-100"
        }, ticketing.Calls);
        Assert.Equal("TLB-10", ticketing.Tickets["TLB-100"].ParentId);
    }

    [Fact]
    public async Task CrossProjectParent_FailsScopedResolutionBeforeTicketCreation()
    {
        var ticketing = new RecordingTicketing();
        ticketing.Seed("OTHER-10", "uuid-other-10"); // Ordinary GetAsync would resolve this alias.
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => phase.RunFromStructuredAsync(
            "child", null, "body", null, null, parentId: "OTHER-10", ct: CancellationToken.None));

        Assert.Contains("outside configured project", error.Message);
        Assert.Empty(ticketing.Created);
        Assert.Equal(new[] { "get-relation:OTHER-10" }, ticketing.Calls);
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

    [Fact]
    public async Task Relations_AllTargetsResolveBeforeCreate_ThenEdgesAreCreated()
    {
        var ticketing = new RecordingTicketing();
        ticketing.Seed("TLB-8", "uuid-8");
        ticketing.Seed("TLB-9", "uuid-9");
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var result = await phase.RunFromStructuredAsync("child", null, "body", null, null, null,
            new[] { new Relation("blocked-by", "TLB-8"), new Relation("implements", "TLB-9") },
            CancellationToken.None);

        Assert.Equal(new[]
        {
            "get:TLB-8", "get:TLB-9", "create",
            "relate:TLB-100:blocked_by:TLB-8", "relate:TLB-100:implements:TLB-9",
            "get:TLB-100", "relations:TLB-100"
        }, ticketing.Calls);
        Assert.Equal(new[] { "blocked_by", "implements" }, result.Ticket!.Relations.Select(r => r.Kind));
    }

    [Fact]
    public async Task UnknownRelationTarget_FailsBeforeTicketCreation()
    {
        var ticketing = new RecordingTicketing();
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => phase.RunFromStructuredAsync(
            "child", null, "body", null, null, null,
            new[] { new Relation("relates_to", "TLB-999") }, CancellationToken.None));

        Assert.Empty(ticketing.Created);
        Assert.Equal(new[] { "get:TLB-999" }, ticketing.Calls);
    }

    [Fact]
    public async Task CrossProjectRelationTarget_FailsScopedResolutionBeforeTicketCreation()
    {
        var ticketing = new RecordingTicketing();
        ticketing.Seed("OTHER-8", "uuid-other-8"); // Ordinary GetAsync would resolve this alias.
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => phase.RunFromStructuredAsync(
            "child", null, "body", null, null, null,
            new[] { new Relation("relates_to", "OTHER-8") }, CancellationToken.None));

        Assert.Contains("outside configured project", error.Message);
        Assert.Empty(ticketing.Created);
        Assert.Equal(new[] { "get-relation:OTHER-8" }, ticketing.Calls);
    }

    [Fact]
    public async Task LaterRelationFailure_NamesCreatedTicketAndPossibleEarlierEdges()
    {
        var ticketing = new RecordingTicketing { RelationErrorTarget = "TLB-9" };
        ticketing.Seed("TLB-8", "uuid-8");
        ticketing.Seed("TLB-9", "uuid-9");
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            phase.RunFromStructuredAsync("child", null, "body", null, null, null,
                new[] { new Relation("blocking", "TLB-8"), new Relation("implements", "TLB-9") },
                CancellationToken.None));

        Assert.Contains("Ticket TLB-100 was created", error.Message);
        Assert.Contains("Earlier relation edges may already exist", error.Message);
        Assert.Contains("build relate TLB-100 --list", error.Message);
        Assert.Single(ticketing.Created);
        Assert.Contains("relate:TLB-100:blocking:TLB-8", ticketing.Calls);
    }

    [Fact]
    public async Task ParentMutationFailure_NamesCreatedTicket()
    {
        var ticketing = new RecordingTicketing { ParentMutationFails = true };
        ticketing.Seed("TLB-10", "uuid-10");
        var phase = new NewPhase(ticketing, new NullEventSink(), MakeOptions());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            phase.RunFromStructuredAsync("child", null, "body", null, null, "TLB-10", CancellationToken.None));

        Assert.Contains("Ticket TLB-100 was created", error.Message);
        Assert.Contains("setting parent to TLB-10 failed", error.Message);
        Assert.DoesNotContain("relate:", string.Join("\n", ticketing.Calls));
    }

    private sealed record CreatedTicket(string Title, string? Type, string Html, IReadOnlyList<string>? Labels);

    private sealed class RecordingTicketing : ITicketing
    {
        public List<string> Calls { get; } = new();
        public List<CreatedTicket> Created { get; } = new();
        public Dictionary<string, Ticket> Tickets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? RelationErrorTarget { get; init; }
        public bool ParentMutationFails { get; init; }
        private readonly Dictionary<string, string> _uuidById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _idByUuid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Relation>> _relationsBySource = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string id, string uuid)
        {
            _uuidById[id] = uuid;
            _idByUuid[uuid] = id;
            Tickets[id] = new Ticket(id, uuid, "Parent", "", TicketState.Backlog,
                Size.M, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), null);
        }

        public BackendCapabilities Capabilities => new(true, true, true, true);

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            Calls.Add($"get:{id}");
            if (!Tickets.TryGetValue(id, out var ticket))
                throw new KeyNotFoundException($"Issue {id} not found");
            return Task.FromResult(ticket);
        }

        public Task<Ticket> GetRelationTicketAsync(string id, CancellationToken ct)
        {
            if (id.StartsWith("OTHER-", StringComparison.OrdinalIgnoreCase))
            {
                Calls.Add($"get-relation:{id}");
                throw new KeyNotFoundException($"Ticket '{id}' is outside configured project 'TLB'");
            }
            return GetAsync(id, ct);
        }

        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct)
        {
            Calls.Add("create");
            Created.Add(new CreatedTicket(title, type, descriptionHtml, initialLabelNames));
            _uuidById["TLB-100"] = "uuid-100";
            _idByUuid["uuid-100"] = "TLB-100";
            Tickets["TLB-100"] = new Ticket("TLB-100", "uuid-100", title, type ?? string.Empty, TicketState.Backlog,
                Size.M, Risk.Medium, descriptionHtml, Array.Empty<Relation>(),
                initialLabelNames ?? Array.Empty<string>(), null);
            return Task.FromResult(new NewTicketResult("TLB-100", "uuid-100", new DateTime(2026, 1, 1)));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            Calls.Add($"setparent:{childUuid}->{parentUuid}");
            if (ParentMutationFails)
                throw new InvalidOperationException("backend rejected parent");
            if (!_idByUuid.TryGetValue(childUuid, out var childId) || !_idByUuid.TryGetValue(parentUuid, out var parentId))
                throw new KeyNotFoundException("parent or child not found");
            Tickets[childId] = Tickets[childId] with { ParentId = parentId };
            return Task.CompletedTask;
        }

        public Task CreateRelationAsync(string sourceId, string relationKind, string targetId, CancellationToken ct)
        {
            Calls.Add($"relate:{sourceId}:{relationKind}:{targetId}");
            if (targetId == RelationErrorTarget)
                throw new InvalidOperationException("backend rejected relation");
            if (!_relationsBySource.TryGetValue(sourceId, out var list))
            {
                list = new List<Relation>();
                _relationsBySource[sourceId] = list;
            }
            list.Add(new Relation(relationKind, targetId, $"edge-{list.Count + 1}"));
            return Task.CompletedTask;
        }

        // Unused by RunFromStructuredAsync.
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => throw new NotImplementedException();
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
        {
            Calls.Add($"relations:{id}");
            return Task.FromResult<IReadOnlyList<Relation>>(
                _relationsBySource.TryGetValue(id, out var list) ? list.AsReadOnly() : Array.Empty<Relation>());
        }
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
