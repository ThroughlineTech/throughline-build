using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.JudgmentSlots;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class ReopenCommandTests
{
    private static Ticket MakeTicket(
        TicketState state,
        string descriptionHtml = "<p>desc</p>") => new Ticket(
        Id: "TLB-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: descriptionHtml,
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static TicketCommandContext MakeCtx(
        string ticketId = "TLB-1",
        Dictionary<string, string>? args = null) =>
        new TicketCommandContext(ticketId, args ?? new Dictionary<string, string>());

    private static (ReopenCommand cmd, FakeTicketing ticketing, FakeEventSink events)
        BuildCommand(Ticket ticket, List<TicketComment>? existingComments = null)
    {
        var ticketing = new FakeTicketing(ticket);
        if (existingComments is not null)
            ticketing.ExistingComments = existingComments;
        var events = new FakeEventSink();
        var llm = new FakeLlmClient("translated reason");
        var translator = new ReasonTranslator(llm);
        var cmd = new ReopenCommand(ticketing, events, translator);
        return (cmd, ticketing, events);
    }

    [Fact]
    public async Task Done_calls_TransitionLifecycleAsync_with_Reopen()
    {
        var (cmd, ticketing, _) = BuildCommand(MakeTicket(TicketState.Done));

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Cancelled_deferred_with_plan_calls_TransitionLifecycleAsync_with_Reopen()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>deferred:</strong> blocked on upstream</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        var ticket = MakeTicket(
            TicketState.Cancelled,
            descriptionHtml: "<p>summary</p><h3>Implementation Plan</h3><p>steps</p>");
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Cancelled_deferred_no_plan_calls_TransitionLifecycleAsync_with_Reopen()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>deferred:</strong> blocked on upstream</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        var ticket = MakeTicket(
            TicketState.Cancelled,
            descriptionHtml: "<p>just a summary, no plan</p>");
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Cancelled_wontfix_calls_TransitionLifecycleAsync_with_Reopen()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>wontfix:</strong> out of scope</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        // Even with an Implementation Plan section, wontfix routes to Backlog via TransitionLifecycleAsync.
        var ticket = MakeTicket(
            TicketState.Cancelled,
            descriptionHtml: "<p>summary</p><h3>Implementation Plan</h3><p>steps</p>");
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Doubt_calls_TransitionLifecycleAsync_with_Reopen()
    {
        // Cancelled, but no parseable terminal marker in comments - defaults to Backlog.
        var ticket = MakeTicket(TicketState.Cancelled);
        var (cmd, ticketing, _) = BuildCommand(ticket);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Active_rejected()
    {
        var (cmd, ticketing, events) = BuildCommand(MakeTicket(TicketState.Ready));

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("already active", result.Message!);
        Assert.Empty(ticketing.LifecycleTransitions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Transition_is_Reopen_literal()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>deferred:</strong> earlier</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        var ticket = MakeTicket(TicketState.Cancelled);
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        // The transition must be Reopen (comment body is handled internally by TransitionLifecycleAsync).
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Description_not_modified()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>wontfix:</strong> earlier</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        var ticket = MakeTicket(TicketState.Cancelled);
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, ticketing.AppendDescriptionCalls);
        Assert.Equal(0, ticketing.ApplyLabelsCalls);
    }

    [Fact]
    public async Task Most_recent_marker_wins_in_scan()
    {
        // older deferred, newer wontfix -> destination should be Backlog (wontfix wins).
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "older",
                "<p><strong>deferred:</strong> first attempt</p>",
                DateTimeOffset.UtcNow.AddDays(-2)),
            new TicketComment(
                "newer",
                "<p><strong>wontfix:</strong> abandoned</p>",
                DateTimeOffset.UtcNow.AddDays(-1))
        };
        // Even with an Implementation Plan in description, newer wontfix wins.
        var ticket = MakeTicket(
            TicketState.Cancelled,
            descriptionHtml: "<p>summary</p><h3>Implementation Plan</h3><p>steps</p>");
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Reopen, ticketing.LifecycleTransitions[0].transition);
        // Reason should include "from wontfix:" marker.
        Assert.Contains("from wontfix:", ticketing.LifecycleTransitions[0].reason);
    }

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;

        public List<(string id, string html)> Comments { get; } = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, LifecycleTransition transition, string? reason)> LifecycleTransitions { get; } = new();
        public int AppendDescriptionCalls { get; private set; }
        public int ApplyLabelsCalls { get; private set; }
        public List<TicketComment> ExistingComments { get; set; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_ticket);

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            AppendDescriptionCalls++;
            return Task.CompletedTask;
        }

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            Comments.Add((id, html));
            return Task.FromResult($"comment-{Comments.Count}");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
        {
            ApplyLabelsCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)ExistingComments);

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

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct)
        {
            LifecycleTransitions.Add((id, transition, reason));
            return Task.CompletedTask;
        }

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string _content;
        public FakeLlmClient(string content) { _content = content; }

        public Task<LlmResponse> InvokeAsync(
            string modelId,
            IReadOnlyList<LlmMessage> messages,
            InvocationOptions options,
            CancellationToken ct)
        {
            return Task.FromResult(new LlmResponse(_content, new LlmUsage(0, 0, null, null)));
        }

        public async IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
            string modelId,
            IReadOnlyList<LlmMessage> messages,
            InvocationOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamTextDelta(_content);
            yield return new LlmStreamDone();
            await Task.CompletedTask;
        }
    }
}
