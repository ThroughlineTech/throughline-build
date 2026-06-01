using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the inject-pointer behavior added by Brief 09 (Plan C):
///   (1) A non-empty ChainCommitRange on ImplementPhaseOptions threads through to the
///       brief's RelevantFiles and Context["chain_pointer"].
///   (2) A null or empty range produces a brief with no pointer and empty RelevantFiles,
///       identical to the no-pointer baseline.
///
/// Tests are hermetic: no real git process; the brief is captured by the worker fake.
/// </summary>
public class InjectPointerPhaseTests
{
    private const string MainSha  = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";
    private const string StartSha = "1111111111111111111111111111111111111111";
    private const string EndSha   = "2222222222222222222222222222222222222222";

    private static Ticket MakeTicket(TicketState state = TicketState.Ready) => new Ticket(
        Id: "TLB-2",
        Uuid: "ticket-uuid-2",
        Title: "Second ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>second</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-pointer-test",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Second.cs" }, null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Second.cs" }
        });

    // -----------------------------------------------------------------------
    // Acceptance: ImplementPhaseOptions.ChainCommitRange threads to the brief
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_WithNonEmptyChainCommitRange_BriefRelevantFilesPopulated()
    {
        // Arrange: a ChainCommitRange representing one shipped sibling.
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/First.cs", "tests/FirstTests.cs" });

        var phaseOpts = new ImplementPhaseOptions(
            ReviewFeedback: null,
            SharedWorktreePath: null,
            ChainCommitRange: range);

        var ticketing = new IpFakeTicketing(MakeTicket());
        var worker = new IpCapturingWorkerAgent(OkWorkerResult());
        var events = new IpFakeEventSink();
        var git = new IpFakeGitClient(MainSha, CommitSha);

        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOpts);

        // Act
        var result = await phase.RunAsync("TLB-2", Directory.GetCurrentDirectory(), CancellationToken.None);

        // Assert
        Assert.True(result.Success, $"Expected success but got: {result.FailureReason}");
        Assert.NotNull(worker.LastBrief);

        var brief = worker.LastBrief!;
        Assert.Equal(new[] { "src/First.cs", "tests/FirstTests.cs" }, brief.RelevantFiles);
    }

    [Fact]
    public async Task RunAsync_WithNonEmptyChainCommitRange_BriefContextContainsPointer()
    {
        var range = new ChainCommitRange(
            StartSha: StartSha,
            EndSha: EndSha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/First.cs" });

        var phaseOpts = new ImplementPhaseOptions(
            ReviewFeedback: null,
            SharedWorktreePath: null,
            ChainCommitRange: range);

        var ticketing = new IpFakeTicketing(MakeTicket());
        var worker = new IpCapturingWorkerAgent(OkWorkerResult());
        var events = new IpFakeEventSink();
        var git = new IpFakeGitClient(MainSha, CommitSha);

        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOpts);

        var result = await phase.RunAsync("TLB-2", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        var brief = worker.LastBrief!;
        Assert.True(brief.Context.ContainsKey("chain_pointer"),
            "Brief Context should contain 'chain_pointer' when ChainCommitRange is non-empty");
        Assert.Contains(StartSha, brief.Context["chain_pointer"]);
        Assert.Contains(EndSha, brief.Context["chain_pointer"]);
    }

    // -----------------------------------------------------------------------
    // Acceptance: null range leaves the brief unchanged (no-pointer baseline)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_WithNullChainCommitRange_BriefRelevantFilesEmpty()
    {
        var phaseOpts = new ImplementPhaseOptions(
            ReviewFeedback: null,
            SharedWorktreePath: null,
            ChainCommitRange: null);

        var ticketing = new IpFakeTicketing(MakeTicket());
        var worker = new IpCapturingWorkerAgent(OkWorkerResult());
        var events = new IpFakeEventSink();
        var git = new IpFakeGitClient(MainSha, CommitSha);

        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOpts);

        var result = await phase.RunAsync("TLB-2", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(worker.LastBrief!.RelevantFiles);
    }

    [Fact]
    public async Task RunAsync_WithNullChainCommitRange_BriefContextLacksPointer()
    {
        var phaseOpts = new ImplementPhaseOptions(
            ReviewFeedback: null,
            SharedWorktreePath: null,
            ChainCommitRange: null);

        var ticketing = new IpFakeTicketing(MakeTicket());
        var worker = new IpCapturingWorkerAgent(OkWorkerResult());
        var events = new IpFakeEventSink();
        var git = new IpFakeGitClient(MainSha, CommitSha);

        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOpts);

        var result = await phase.RunAsync("TLB-2", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(worker.LastBrief!.Context.ContainsKey("chain_pointer"),
            "Context must not contain 'chain_pointer' when ChainCommitRange is null");
    }

    [Fact]
    public async Task RunAsync_DefaultPhaseOptions_BriefRelevantFilesEmpty()
    {
        // Default ImplementPhaseOptions has no ChainCommitRange - pointer must be absent.
        var ticketing = new IpFakeTicketing(MakeTicket());
        var worker = new IpCapturingWorkerAgent(OkWorkerResult());
        var events = new IpFakeEventSink();
        var git = new IpFakeGitClient(MainSha, CommitSha);

        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-2", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(worker.LastBrief!.RelevantFiles);
        Assert.False(worker.LastBrief!.Context.ContainsKey("chain_pointer"));
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    private sealed class IpCapturingWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public Brief? LastBrief { get; private set; }

        public IpCapturingWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastBrief = brief;
            return Task.FromResult(_result);
        }
    }

    private sealed class IpFakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        public List<(string id, string html)> Comments { get; } = new();

        public IpFakeTicketing(Ticket ticket) { _ticket = ticket; }

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
            Task.FromResult((IReadOnlyList<TicketComment>)_seededComments);
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
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class IpFakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class IpFakeGitClient : IGitClient
    {
        private readonly string _mainSha;
        private readonly string _headSha;

        public IpFakeGitClient(string mainSha, string headSha)
        {
            _mainSha = mainSha;
            _headSha = headSha;
        }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
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
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
