using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

[Collection("CommandConsoleTests")]
public class DeferCommandTests
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

    private static (DeferCommand cmd, FakeTicketing ticketing, FakeEventSink events,
        FakeGitClient git, FakeLlmClient llm, string mainPath)
        BuildCommand(
            Ticket ticket,
            string mainPath,
            FakeGitClient? gitClient = null,
            FakeLlmClient? llmClient = null,
            FakeTicketing? ticketingClient = null)
    {
        var ticketing = ticketingClient ?? new FakeTicketing(ticket);
        var events = new FakeEventSink();
        var git = gitClient ?? new FakeGitClient();
        var llm = llmClient ?? new FakeLlmClient("translated reason");
        var translator = new ReasonTranslator(llm);
        var decrufter = new WorktreeDecrufter(git);
        var cmd = new DeferCommand(ticketing, events, git, translator, decrufter, mainPath);
        return (cmd, ticketing, events, git, llm, mainPath);
    }

    [Fact]
    public async Task HappyPath_posts_deferred_comment_transitions_to_cancelled()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "raw reason text" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        // Comment body must be LITERALLY the deferred marker.
        Assert.Equal("<p><strong>deferred:</strong> translated reason</p>", ticketing.Comments[0].html);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Cancelled, ticketing.Transitions[0].state);
        Assert.Equal(1, ticketing.RollupCalls);
        // No worktree directory present, so decrufter should not be invoked.
        // (We assert this indirectly: no ListWorktrees call from decrufter.)
        Assert.Equal(0, ticketing.ListWorktreeCallsViaGit);
    }

    [Fact]
    public async Task NoReason_returns_error()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string>());
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("reason is required", result.Message);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(ticketing.Transitions);
        Assert.Equal(0, ticketing.RollupCalls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Terminal_Done_rejected_no_writes()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(
            MakeTicket(state: TicketState.Done), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("already terminal", result.Message);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(ticketing.Transitions);
        Assert.Equal(0, ticketing.RollupCalls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Terminal_Cancelled_rejected_no_writes()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(
            MakeTicket(state: TicketState.Cancelled), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("already terminal", result.Message);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(ticketing.Transitions);
        Assert.Equal(0, ticketing.RollupCalls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task UnmergedBranches_warning_does_not_block()
    {
        using var tmp = new TempDir();
        var git = new FakeGitClient { UnmergedBranches = new List<string> { "ticket/tlb-1-foo" } };
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path, gitClient: git);

        var originalErr = Console.Error;
        var swErr = new StringWriter();
        Console.SetError(swErr);
        try
        {
            var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);
            Assert.True(result.Success);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Contains("ticket/tlb-1-foo", swErr.ToString());
        Assert.Contains("WARNING", swErr.ToString());
        // Action still proceeded to Cancelled.
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Cancelled, ticketing.Transitions[0].state);
        Assert.Single(ticketing.Comments);
    }

    [Fact]
    public async Task Rollup_failure_does_not_unwind()
    {
        using var tmp = new TempDir();
        var ticketing = new FakeTicketing(MakeTicket()) { RollupThrows = true };
        var (cmd, _, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path, ticketingClient: ticketing);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Cancelled, ticketing.Transitions[0].state);
        Assert.Single(ticketing.Comments);
        // Rollup attempted (and threw) - command should still have completed.
        Assert.Equal(1, ticketing.RollupCalls);
    }

    [Fact]
    public async Task WorktreeDecruft_noop_when_path_absent()
    {
        using var tmp = new TempDir();
        // mainPath/.worktrees/ticket-tlb-1 does NOT exist.
        var git = new FakeGitClient();
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path, gitClient: git);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        // Decrufter would call ListWorktreesAsync as its first step; we should NOT
        // see any such call because the directory check short-circuits before
        // DecruftAsync is invoked.
        Assert.Equal(0, git.ListWorktreesCalls);
    }

    [Fact]
    public async Task Marker_is_deferred_not_wontfix()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        var body = ticketing.Comments[0].html;
        Assert.Contains("<strong>deferred:</strong>", body);
        Assert.DoesNotContain("<strong>wontfix:</strong>", body);
        Assert.DoesNotContain("<strong>reopened:</strong>", body);
    }
}
