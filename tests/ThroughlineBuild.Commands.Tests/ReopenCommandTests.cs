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
    public async Task Done_transitions_to_Backlog()
    {
        var (cmd, ticketing, _) = BuildCommand(MakeTicket(TicketState.Done));

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Backlog, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task Cancelled_deferred_with_plan_transitions_to_Ready()
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
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Ready, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task Cancelled_deferred_no_plan_transitions_to_Backlog()
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
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Backlog, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task Cancelled_wontfix_transitions_to_Backlog()
    {
        var existing = new List<TicketComment>
        {
            new TicketComment(
                "c1",
                "<p><strong>wontfix:</strong> out of scope</p>",
                DateTimeOffset.UtcNow.AddHours(-1))
        };
        // Even with an Implementation Plan section, wontfix routes to Backlog.
        var ticket = MakeTicket(
            TicketState.Cancelled,
            descriptionHtml: "<p>summary</p><h3>Implementation Plan</h3><p>steps</p>");
        var (cmd, ticketing, _) = BuildCommand(ticket, existing);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Backlog, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task Doubt_defaults_to_Backlog()
    {
        // Cancelled, but no parseable terminal marker in comments.
        var ticket = MakeTicket(TicketState.Cancelled);
        var (cmd, ticketing, _) = BuildCommand(ticket);

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Backlog, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task Active_rejected()
    {
        var (cmd, ticketing, events) = BuildCommand(MakeTicket(TicketState.Ready));

        var result = await cmd.ExecuteAsync(MakeCtx(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("already active", result.Message!);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Marker_is_reopened_literal()
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
        Assert.Single(ticketing.Comments);
        var body = ticketing.Comments[0].html;
        // The leading marker MUST be reopened.
        Assert.StartsWith("<p><strong>reopened:</strong>", body);
        // The "from X" suffix may mention deferred, but no leading deferred/wontfix.
        Assert.DoesNotContain("<p><strong>deferred:</strong>", body);
        Assert.DoesNotContain("<p><strong>wontfix:</strong>", body);
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
        // older deferred, newer wontfix -> destination should be Backlog (wontfix).
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
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Backlog, ticketing.Transitions[0].state);
        Assert.Contains("from wontfix:", ticketing.Comments[0].html);
    }
}
