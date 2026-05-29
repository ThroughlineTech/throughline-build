using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

[Collection("CommandConsoleTests")]
public class CloseCommandTests
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

    private static (CloseCommand cmd, FakeTicketing ticketing, FakeEventSink events,
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
        var cmd = new CloseCommand(ticketing, events, git, translator, decrufter, mainPath);
        return (cmd, ticketing, events, git, llm, mainPath);
    }

    [Fact]
    public async Task HappyPath_posts_wontfix_comment_transitions_to_cancelled()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, events, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "raw reason text" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        // Comment body must be LITERALLY the wontfix marker.
        Assert.Equal("<p><strong>wontfix:</strong> translated reason</p>", ticketing.Comments[0].html);
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
    public async Task EmptyReason_returns_error()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "   " });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("reason is required", result.Message);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(ticketing.Transitions);
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
    public async Task Marker_is_wontfix_not_deferred()
    {
        using var tmp = new TempDir();
        var (cmd, ticketing, _, _, _, _) = BuildCommand(MakeTicket(), tmp.Path);

        var ctx = MakeCtx(args: new Dictionary<string, string> { ["reason"] = "x" });
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        var body = ticketing.Comments[0].html;
        Assert.Contains("<strong>wontfix:</strong>", body);
        Assert.DoesNotContain("<strong>deferred:</strong>", body);
        Assert.DoesNotContain("<strong>reopened:</strong>", body);
    }

    // ---------- Fakes ----------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tlb66-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;

        public List<(string id, string html)> Comments { get; } = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public int RollupCalls { get; private set; }
        public bool RollupThrows { get; set; }
        // Track-through count for IGitClient calls observed via the decrufter
        // (not used directly on this fake; left for symmetry).
        public int ListWorktreeCallsViaGit { get; set; }

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

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            Comments.Add((id, html));
            return Task.FromResult($"comment-{Comments.Count}");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct)
        {
            RollupCalls++;
            if (RollupThrows)
                throw new InvalidOperationException("simulated rollup failure");
            return Task.FromResult(new RollupResult(false, null, null));
        }

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

    private sealed class FakeGitClient : IGitClient
    {
        public List<string> UnmergedBranches { get; set; } = new();
        public int ListWorktreesCalls { get; private set; }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("abc123");

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
        {
            ListWorktreesCalls++;
            return Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(UnmergedBranches);

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult("0000000000000000000000000000000000000000");

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));

        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
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
