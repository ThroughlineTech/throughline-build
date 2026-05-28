using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ImplementPhaseReworkTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket(TicketState state) => new Ticket(
        Id: "TLB-1",
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
        SessionId: "session-rework",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs" }
        });

    private static ReviewFeedback MakeReviewFeedback(int round = 1) => new ReviewFeedback(
        Rationale: "Tests were not written.",
        ChecksFailed: new[] { "tests_pass", "coverage_ok" },
        ReworkRoundNumber: round);

    [Fact]
    public async Task RunAsync_ReworkRound_InProgressWithFeedback_Succeeds()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, result.ReworkRoundNumber);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_SkipsStartTransitionAndEndsInReview()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_InitialRound_ReadyWithNoFeedback_Succeeds()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ReworkRoundNumber);
        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[1].state);
    }

    [Fact]
    public async Task RunAsync_InitialRound_AgainstInProgress_FailsClearly()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Ready", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_AgainstReady_FailsClearly()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("InProgress", result.FailureReason ?? "");
        Assert.Contains("rework", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_ReworkRoundNumberPropagatesFromFeedbackToResult()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(2));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ReworkRoundNumber);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }
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
            string title, string type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "fake";
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

        public FakeGitClient(string mainSha, string headSha)
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
