using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ReworkPhaseTests
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

    private static WorkerResult OkWorkerResult(string commitSha = CommitSha) => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = commitSha,
            ["files_changed"] = new[] { "src/Foo.cs" }
        });

    private static WorkerResult FailedWorkerResult() => new WorkerResult(
        Status.Failed, "boom", Array.Empty<string>(), "worker exploded",
        new Dictionary<string, object>());

    private static ReviewFeedback MakeFeedback(int roundNumber = 1) =>
        new ReviewFeedback("The code has issues", new[] { "test-failure" }, roundNumber);

    private static ReworkPhaseOptions MakePhaseOptions(
        string? manualFeedback = null,
        int reworkRoundNumber = 1) =>
        new ReworkPhaseOptions("TLB-1", manualFeedback, reworkRoundNumber, false);

    [Fact]
    public async Task RunAsync_HappyPath_TicketInProgress_RetrieverReturnsRework_ImplementSucceeds_OutcomeImplemented()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(MakeFeedback());
        var phase = new ReworkPhase(ticketing, worker, events, MakeOptions(), retriever, MakePhaseOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.Implemented, result.Outcome);
        Assert.Equal("event-log", result.FeedbackSource);
        Assert.NotNull(result.ImplementResult);
        Assert.True(result.ImplementResult!.Success);
        Assert.Null(result.FailureReason);
        Assert.Equal("TLB-1", result.TicketId);
    }

    [Fact]
    public async Task RunAsync_ManualFeedback_SkipsRetrieverAndUsesManualText()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(MakeFeedback());
        var phase = new ReworkPhase(
            ticketing, worker, events, MakeOptions(), retriever,
            MakePhaseOptions(manualFeedback: "Please fix the null ref on line 42"), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.Implemented, result.Outcome);
        Assert.Equal("manual", result.FeedbackSource);
        Assert.NotNull(result.ImplementResult);
        Assert.True(result.ImplementResult!.Success);
        // Retriever was never called since ManualFeedback was supplied
        Assert.Equal(0, retriever.CallCount);
    }

    [Fact]
    public async Task RunAsync_TicketNotInProgress_ReturnsTicketNotInProgress_NoImplementInvocation()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Done));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(MakeFeedback());
        var phase = new ReworkPhase(ticketing, worker, events, MakeOptions(), retriever, MakePhaseOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.TicketNotInProgress, result.Outcome);
        Assert.Contains("this is unexpected", result.FailureReason ?? "");
        Assert.Null(result.ImplementResult);
        Assert.Equal(0, worker.CallCount);
        Assert.Equal(0, retriever.CallCount);
    }

    [Fact]
    public async Task RunAsync_TicketInReady_ReturnsTicketNotInProgress()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(null);
        var phase = new ReworkPhase(ticketing, worker, events, MakeOptions(), retriever, MakePhaseOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.TicketNotInProgress, result.Outcome);
        Assert.Contains("this is unexpected", result.FailureReason ?? "");
        Assert.Equal(0, worker.CallCount);
        Assert.Equal(0, retriever.CallCount);
    }

    [Fact]
    public async Task RunAsync_NoFeedbackInEventLogAndNoManual_ReturnsNoFeedbackAvailable()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(null);
        var phase = new ReworkPhase(ticketing, worker, events, MakeOptions(), retriever, MakePhaseOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.NoFeedbackAvailable, result.Outcome);
        Assert.Contains("--feedback", result.FailureReason ?? "");
        Assert.Null(result.ImplementResult);
        Assert.Equal(0, worker.CallCount);
    }

    [Fact]
    public async Task RunAsync_ImplementFails_ReturnsImplementFailed_WithFailureReason()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(FailedWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(MakeFeedback());
        var phase = new ReworkPhase(ticketing, worker, events, MakeOptions(), retriever, MakePhaseOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.ImplementFailed, result.Outcome);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("worker exploded", result.FailureReason ?? "");
        Assert.NotNull(result.ImplementResult);
        Assert.False(result.ImplementResult!.Success);
        Assert.Contains("worker exploded", result.ImplementResult.FailureReason ?? "");
    }

    [Fact]
    public async Task RunAsync_ReworkRoundNumberFromOptions_PropagatesToImplementPhase()
    {
        // Retriever returns feedback with ReworkRoundNumber=1 (its default)
        // Options supply ReworkRoundNumber=2
        // ImplementPhase returns ReworkRoundNumber from ReviewFeedback -- should be 2
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var retriever = new FakeRetriever(MakeFeedback(roundNumber: 1));
        var phase = new ReworkPhase(
            ticketing, worker, events, MakeOptions(), retriever,
            MakePhaseOptions(reworkRoundNumber: 2), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(ReworkOutcome.Implemented, result.Outcome);
        Assert.NotNull(result.ImplementResult);
        // ImplementPhase returns ReviewFeedback.ReworkRoundNumber via ImplementResult.ReworkRoundNumber
        Assert.Equal(2, result.ImplementResult!.ReworkRoundNumber);
    }

    // --- Fakes ---

    private sealed class FakeRetriever : IReviewFeedbackRetriever
    {
        private readonly ReviewFeedback? _feedback;
        public int CallCount { get; private set; }

        public FakeRetriever(ReviewFeedback? feedback) { _feedback = feedback; }

        public ReviewFeedback? GetLatestRework(string ticketId)
        {
            CallCount++;
            return _feedback;
        }
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public FakeTicketing(Ticket ticket) { _ticket = ticket; }
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Task.CompletedTask;
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => Task.FromResult("c-1");
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
        public int CallCount { get; private set; }
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "fake";
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
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
