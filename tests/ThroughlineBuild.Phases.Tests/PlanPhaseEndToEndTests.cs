using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

// End-to-end tests that verify the full plan_body_ref -> FencedBlockResolver
// -> MarkdownRenderer -> AppendDescriptionAsync pipeline, including the
// shell-snippet canary case (block content containing unescaped quotes).
public class PlanPhaseEndToEndTests
{
    private static Ticket MakeTicket(TicketState state = TicketState.Backlog) => new Ticket(
        Id: "TLB-1",
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-e2e",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    // Canary: plan body containing shell snippets with unescaped double-quotes and
    // backtick inline code. The old plan_html path embedded pre-rendered HTML inside
    // JSON metadata (a string-within-string encoding hazard). The new path carries
    // raw markdown in a fenced block and renders to HTML in-process, so there is
    // no JSON-escape round-trip for the content.
    [Fact]
    public async Task RunAsync_ShellSnippetPlanBody_RendersHtmlWithCodeSpans()
    {
        const string planMarkdown =
            "# Plan\n" +
            "Shell snippet: `echo \"hello world\"`\n" +
            "Use `dotnet test`.";

        var metadata = new Dictionary<string, object>
        {
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "S",
            ["planned_at_sha"] = "canary-sha"
        };
        var blocks = new Dictionary<string, string>
        {
            ["PLAN_BODY"] = planMarkdown
        };

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata, blocks));
        var events = new FakeEventSink();
        var git = new FakeGitClient("canary-sha");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success, $"Expected success but got failure: {result.FailureReason}");
        Assert.Single(ticketing.AppendDescriptions);

        var html = ticketing.AppendDescriptions[0].html;
        // Heading rendered
        Assert.Contains("<h1>Plan</h1>", html);
        // Inline code spans rendered (HTML-escaped quotes inside code)
        Assert.Contains("<code>", html);
        Assert.Contains("echo", html);
        Assert.Contains("dotnet test", html);
        // Double-quotes inside code are HTML-escaped (no raw " in code spans)
        Assert.Contains("&quot;", html);
    }

    [Fact]
    public async Task RunAsync_MissingPlanBodyRef_ReturnsFailureWithClearMessage()
    {
        var metadata = new Dictionary<string, object>
        {
            // plan_body_ref intentionally absent
            ["risk_label"] = "low",
            ["size_label"] = "S",
            ["planned_at_sha"] = "sha-abc"
        };
        var blocks = new Dictionary<string, string>();

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata, blocks));
        var events = new FakeEventSink();
        var git = new FakeGitClient("sha-abc");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("plan_body_ref", result.FailureReason);
        Assert.Empty(ticketing.AppendDescriptions);
    }

    [Fact]
    public async Task RunAsync_PlanBodyRefPointsToMissingBlock_ReturnsFailureWithClearMessage()
    {
        var metadata = new Dictionary<string, object>
        {
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "S",
            ["planned_at_sha"] = "sha-abc"
        };
        // Blocks does not contain PLAN_BODY
        var blocks = new Dictionary<string, string>();

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata, blocks));
        var events = new FakeEventSink();
        var git = new FakeGitClient("sha-abc");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("PLAN_BODY", result.FailureReason);
        Assert.Empty(ticketing.AppendDescriptions);
    }

    [Fact]
    public async Task RunAsync_NullBlocks_TreatedAsEmpty_ReturnsFailure()
    {
        // WorkerResult.Blocks is null (e.g. legacy worker that does not populate blocks)
        var metadata = new Dictionary<string, object>
        {
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "S",
            ["planned_at_sha"] = "sha-abc"
        };

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata, Blocks: null));
        var events = new FakeEventSink();
        var git = new FakeGitClient("sha-abc");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("plan_body_ref", result.FailureReason);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, string html)> AppendDescriptions { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Task.CompletedTask;
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            AppendDescriptions.Add((id, html));
            return Task.CompletedTask;
        }
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-1");
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Relation>)Array.Empty<Relation>());
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());
        public Task<NewTicketResult> CreateTicketAsync(
            string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
                string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
                Task.FromResult(new CreateChildTicketsResult(
                    children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                    Array.Empty<string>()));
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    private sealed class FakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _sha;
        public FakeGitClient(string sha) { _sha = sha; }
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_sha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
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
}
