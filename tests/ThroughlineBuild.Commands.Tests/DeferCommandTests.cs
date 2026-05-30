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
        Uuid: "test-uuid-1",
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
    public async Task HappyPath_calls_TransitionLifecycleAsync_with_Defer()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "raw reason text" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
        Assert.Equal("translated reason", ticketing.LifecycleTransitions[0].reason);
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
        Assert.Empty(ticketing.LifecycleTransitions);
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
        Assert.Empty(ticketing.LifecycleTransitions);
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
        Assert.Empty(ticketing.LifecycleTransitions);
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
        // Action still proceeded to TransitionLifecycleAsync with Defer.
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
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
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
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
    public async Task Transition_is_Defer_not_Close()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
        Assert.NotEqual(LifecycleTransition.Close, ticketing.LifecycleTransitions[0].transition);
    }

    private static Ticket MakeChild(string id, string uuid, TicketState state) => new Ticket(
        Id: id,
        Uuid: uuid,
        Title: $"Child {id}",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>child</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: "test-uuid-1");

    [Fact]
    public async Task Parent_with_two_backlog_children_cascades_defer_to_all()
    {
        using var tmp = new TempDir();
        var parent = MakeTicket();
        var ticketing = new FakeTicketing(parent);
        ticketing.SeedChildren(new[]
        {
            MakeChild("TLB-2", "child-uuid-2", TicketState.Backlog),
            MakeChild("TLB-3", "child-uuid-3", TicketState.Backlog)
        });
        var (cmd, _, _, _, _, _) = BuildCommand(parent, tmp.Path, ticketingClient: ticketing);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        // 2 children + 1 parent = 3 Defer transitions
        Assert.Equal(3, ticketing.LifecycleTransitions.Count);
        Assert.All(ticketing.LifecycleTransitions, t => Assert.Equal(LifecycleTransition.Defer, t.transition));
        // Children come first, then parent
        Assert.Equal("TLB-2", ticketing.LifecycleTransitions[0].id);
        Assert.Equal("TLB-3", ticketing.LifecycleTransitions[1].id);
        Assert.Equal("TLB-1", ticketing.LifecycleTransitions[2].id);
    }

    [Fact]
    public async Task Parent_with_done_child_skips_terminal_child()
    {
        using var tmp = new TempDir();
        var parent = MakeTicket();
        var ticketing = new FakeTicketing(parent);
        ticketing.SeedChildren(new[]
        {
            MakeChild("TLB-2", "child-uuid-2", TicketState.Cancelled),
            MakeChild("TLB-3", "child-uuid-3", TicketState.Ready)
        });
        var (cmd, _, _, _, _, _) = BuildCommand(parent, tmp.Path, ticketingClient: ticketing);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        // Cancelled child skipped, so 1 non-terminal child + 1 parent = 2 transitions
        Assert.Equal(2, ticketing.LifecycleTransitions.Count);
        Assert.Equal("TLB-3", ticketing.LifecycleTransitions[0].id);
        Assert.Equal("TLB-1", ticketing.LifecycleTransitions[1].id);
        Assert.All(ticketing.LifecycleTransitions, t => Assert.Equal(LifecycleTransition.Defer, t.transition));
    }

    [Fact]
    public async Task No_cascade_flag_skips_children()
    {
        using var tmp = new TempDir();
        var parent = MakeTicket();
        var ticketing = new FakeTicketing(parent);
        ticketing.SeedChildren(new[]
        {
            MakeChild("TLB-2", "child-uuid-2", TicketState.Backlog),
            MakeChild("TLB-3", "child-uuid-3", TicketState.InProgress)
        });
        var (cmd, _, _, _, _, _) = BuildCommand(parent, tmp.Path, ticketingClient: ticketing);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x", ["no-cascade"] = "true" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        // Only parent transition - children skipped due to --no-cascade
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal("TLB-1", ticketing.LifecycleTransitions[0].id);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
    }

    [Fact]
    public async Task Leaf_ticket_no_children_single_transition()
    {
        using var tmp = new TempDir();
        var parent = MakeTicket();
        // No SeedChildren call - QueryAsync returns empty list
        var (cmd, ticketing, _, _, _, _) = BuildCommand(parent, tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.LifecycleTransitions);
        Assert.Equal("TLB-1", ticketing.LifecycleTransitions[0].id);
        Assert.Equal(LifecycleTransition.Defer, ticketing.LifecycleTransitions[0].transition);
    }
}
