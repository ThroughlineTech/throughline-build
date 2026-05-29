using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public class AmendCommandTests
{
    private static Ticket MakeTicket(
        TicketState state = TicketState.Ready,
        IReadOnlyList<string>? labels = null) => new Ticket(
        Id: "TLB-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: labels ?? Array.Empty<string>(),
        ParentId: null);

    private static TicketCommandContext MakeCtx(
        string ticketId = "TLB-1",
        Dictionary<string, string>? args = null) =>
        new TicketCommandContext(ticketId, args ?? new Dictionary<string, string>());

    [Fact]
    public async Task SizeOnly_existing_non_size_labels_preserved()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket", "risk:low", "size:s" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.ApplyLabels);
        var applied = ticketing.ApplyLabels[0].labels;
        Assert.Contains("plan-ticket", applied);
        Assert.Contains("risk:low", applied);
        Assert.Contains("size:m", applied);
        Assert.DoesNotContain("size:s", applied);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Single(events.Events);
        Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
    }

    [Fact]
    public async Task NoteOnly_appends_Context_Note_with_today_date_and_verbatim_note()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var ctx = MakeCtx(args: new Dictionary<string, string> { ["note"] = "rationale text" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.AppendDescriptions);
        var html = ticketing.AppendDescriptions[0].html;
        Assert.Contains(today, html);
        Assert.Contains("rationale text", html);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Single(events.Events);
        Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
    }

    [Fact]
    public async Task SizeAndNote_size_first_then_note()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>
        {
            ["size"] = "L",
            ["note"] = "some rationale"
        });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.ApplyLabels);
        Assert.Single(ticketing.AppendDescriptions);

        // Verify order: size (ApplyLabels) was called before note (AppendDescriptions).
        // Both calls are tracked with a sequence counter in FakeTicketing.
        Assert.True(ticketing.ApplyLabels[0].sequence < ticketing.AppendDescriptions[0].sequence,
            "ApplyLabels must be called before AppendDescriptions");
        Assert.Equal(2, events.Events.Count);
        Assert.All(events.Events, e => Assert.Equal(EventKind.TicketWrite, e.Kind));
    }

    [Fact]
    public async Task Terminal_rejected_no_writes()
    {
        var ticket = MakeTicket(state: TicketState.Done);
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Cancelled or Done", result.Message);
        Assert.Contains("reopen first", result.Message);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Terminal_Cancelled_rejected_no_writes()
    {
        var ticket = MakeTicket(state: TicketState.Cancelled);
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "M" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("Cancelled or Done", result.Message);
        Assert.Contains("reopen first", result.Message);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task MissingFlags_rejected()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string>());
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("at least one", result.Message);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task InvalidSize_rejected()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["size"] = "XL" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task DescriptionOnly_calls_UpdateDescriptionAsync()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<p>new description</p>");
            var ctx = MakeCtx(args: new Dictionary<string, string> { ["description"] = tempFile });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Equal("<p>new description</p>", ticketing.UpdateDescriptions[0].html);
            Assert.Empty(ticketing.ApplyLabels);
            Assert.Empty(ticketing.AppendDescriptions);
            Assert.Single(events.Events);
            Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AcOnly_calls_UpdateDescriptionAsync()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<h3>Acceptance Criteria</h3><ul><li>foo</li></ul>");
            var ctx = MakeCtx(args: new Dictionary<string, string> { ["ac"] = tempFile });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Equal("<h3>Acceptance Criteria</h3><ul><li>foo</li></ul>", ticketing.UpdateDescriptions[0].html);
            Assert.Empty(ticketing.ApplyLabels);
            Assert.Empty(ticketing.AppendDescriptions);
            Assert.Single(events.Events);
            Assert.Equal(EventKind.TicketWrite, events.Events[0].Kind);
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DescriptionAndSize_size_then_description()
    {
        var ticket = MakeTicket(labels: new[] { "plan-ticket" });
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "<p>updated desc</p>");
            var ctx = MakeCtx(args: new Dictionary<string, string>
            {
                ["size"] = "M",
                ["description"] = tempFile
            });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(ticketing.ApplyLabels);
            Assert.Single(ticketing.UpdateDescriptions);
            Assert.Empty(ticketing.AppendDescriptions);

            // Verify order: size (ApplyLabels) was called before description (UpdateDescriptions).
            Assert.True(ticketing.ApplyLabels[0].sequence < ticketing.UpdateDescriptions[0].sequence,
                "ApplyLabels must be called before UpdateDescriptions");
            Assert.Equal(2, events.Events.Count);
            Assert.All(events.Events, e => Assert.Equal(EventKind.TicketWrite, e.Kind));
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MissingDescriptionFile_produces_error()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["description"] = "/nonexistent/path/file.txt" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("failed to read description file", result.Message);
        Assert.Empty(ticketing.UpdateDescriptions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task MissingAcFile_produces_error()
    {
        var ticket = MakeTicket();
        var ticketing = new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var cmd = new AmendCommand(ticketing, events);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["ac"] = "/nonexistent/path/ac.txt" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("failed to read ac file", result.Message);
        Assert.Empty(ticketing.UpdateDescriptions);
        Assert.Empty(events.Events);
    }

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private int _seq;

        public List<(string id, IReadOnlyList<string> labels, int sequence)> ApplyLabels { get; } = new();
        public List<(string id, string html, int sequence)> AppendDescriptions { get; } = new();
        public List<(string id, string html, int sequence)> UpdateDescriptions { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_ticket);

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            AppendDescriptions.Add((id, html, ++_seq));
            return Task.CompletedTask;
        }

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-1");

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
        {
            ApplyLabels.Add((id, labels.ToList(), ++_seq));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

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

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct)
        {
            UpdateDescriptions.Add((id, html, ++_seq));
            return Task.CompletedTask;
        }
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
}
