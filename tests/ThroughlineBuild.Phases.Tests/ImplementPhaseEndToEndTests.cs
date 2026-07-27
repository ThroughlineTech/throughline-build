using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

// End-to-end tests that verify the summary_ref -> FencedBlockResolver
// -> MarkdownRenderer -> CreateCommentAsync pipeline, including the
// shell-snippet canary case (block content containing unescaped quotes,
// backticks, and backslashes that would break JSON string embedding).
public class ImplementPhaseEndToEndTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket(TicketState state = TicketState.Ready) => new Ticket(
        Id: "TLB-1",
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-e2e-impl",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    // Canary: IMPLEMENT_SUMMARY block containing shell snippets with unescaped
    // double-quotes, backtick inline code, and backslash paths. The old approach
    // would embed this content inside a JSON "summary" string, requiring escaping.
    // The new approach carries raw markdown in a fenced block and renders to HTML
    // in-process, so there is no JSON-escape round-trip for the content.
    [Fact]
    public async Task RunAsync_ShellSnippetSummaryBlock_RendersHtmlWithCodeSpansInComment()
    {
        const string summaryMarkdown =
            "# Summary\n" +
            "\n" +
            "Ran `dotnet build` and verified `findstr /c:\"test\"` in CI.\n" +
            "Updated `src\\Foo.cs` and `src\\Bar.cs`.";

        var metadata = new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs", "src/Bar.cs" },
            ["summary_ref"] = "IMPLEMENT_SUMMARY"
        };
        var blocks = new Dictionary<string, string>
        {
            ["IMPLEMENT_SUMMARY"] = summaryMarkdown
        };

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "Implemented TLB-1", new[] { "src/Foo.cs", "src/Bar.cs" }, null, metadata, blocks));
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success, $"Expected success but got: {result.FailureReason}");
        Assert.Single(ticketing.Comments);

        var html = ticketing.Comments[0].html;
        // implemented_at marker is present
        Assert.Contains("implemented_at", html);
        Assert.Contains(CommitSha, html);
        // Summary heading rendered
        Assert.Contains("<h1>Summary</h1>", html);
        // Inline code spans rendered
        Assert.Contains("<code>", html);
        Assert.Contains("dotnet build", html);
        Assert.Contains("findstr", html);
        // Double-quotes inside code are HTML-escaped
        Assert.Contains("&quot;", html);
    }

    [Fact]
    public async Task RunAsync_SummaryRefPointsToMissingBlock_StillSucceeds_WithNoSummaryInComment()
    {
        // If summary_ref points to a block that was not emitted, the phase degrades
        // gracefully: the comment is posted without a summary rather than failing.
        var metadata = new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs" },
            ["summary_ref"] = "IMPLEMENT_SUMMARY"
        };
        // Blocks is empty - the referenced block was not emitted
        var blocks = new Dictionary<string, string>();

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "Implemented TLB-1", new[] { "src/Foo.cs" }, null, metadata, blocks));
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success, $"Expected success but got: {result.FailureReason}");
        Assert.Single(ticketing.Comments);
        var html = ticketing.Comments[0].html;
        // Comment still has the implemented_at marker
        Assert.Contains("implemented_at", html);
        // No extra HTML (no summary)
        Assert.Equal($"<p>[implemented_at: {CommitSha}] (branch ticket/tlb-1)</p>", html);
    }

    [Fact]
    public async Task RunAsync_NullBlocks_StillSucceeds_WithNoSummaryInComment()
    {
        // If Blocks is null (legacy worker), the phase degrades gracefully.
        var metadata = new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs" },
            ["summary_ref"] = "IMPLEMENT_SUMMARY"
        };

        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "Implemented TLB-1", new[] { "src/Foo.cs" }, null, metadata, Blocks: null));
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success, $"Expected success but got: {result.FailureReason}");
        Assert.Single(ticketing.Comments);
        var html = ticketing.Comments[0].html;
        Assert.Contains("implemented_at", html);
        Assert.Equal($"<p>[implemented_at: {CommitSha}] (branch ticket/tlb-1)</p>", html);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Task.CompletedTask;
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            Comments.Add((id, html));
            return Task.FromResult("comment-1");
        }
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
        private readonly string _mainSha;
        private readonly string _headSha;
        public FakeGitClient(string mainSha, string headSha) { _mainSha = mainSha; _headSha = headSha; }
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(_headSha);
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
