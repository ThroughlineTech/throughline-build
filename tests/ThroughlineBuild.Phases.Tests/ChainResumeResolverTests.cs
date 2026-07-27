using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public sealed class ChainResumeResolverTests
{
    private const string TicketId = "TLB-573";
    private const string WorkingDirectory = "/repo";
    private const string TargetBranch = "main";
    private const string SessionId = "chain-session";
    private const string SynthesizedRationale =
        "Resume interrupted implementation: a prior implement round for this ticket did not finish. " +
        "Continue or redo the implementation from the current worktree state.";

    [Fact]
    public async Task ResolveAsync_FreshNonResumeEntry_ReturnsStatePhaseWithoutSideEffects()
    {
        var ticketing = new FakeTicketing();
        var git = new FakeGit(commitsOnBranch: 1);
        var retriever = new FakeFeedbackRetriever(null);
        var (resolver, sink) = MakeResolver(ticketing, git, retriever);

        var entry = await resolver.ResolveAsync(
            MakeTicket(TicketState.Ready),
            WorkingDirectory,
            TargetBranch,
            CancellationToken.None);

        Assert.Equal(StartPhase.Implement, entry.StartPhase);
        Assert.Null(entry.ResumeFeedback);
        Assert.Equal(0, entry.ResumeStartRound);
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(sink.Events);
        Assert.Equal(0, retriever.CallCount);
        Assert.Equal(0, git.RevListCountCallCount);
    }

    [Fact]
    public async Task ResolveAsync_CommittedWork_UsesLatestRecoveredReworkAtRoundOne()
    {
        var recovered = new ReviewFeedback(
            "latest review rationale",
            new[] { "build", "test" },
            ReworkRoundNumber: 7);
        var retriever = new FakeFeedbackRetriever(recovered);
        var (resolver, sink) = MakeResolver(
            new FakeTicketing(),
            new FakeGit(commitsOnBranch: 1),
            retriever);

        var entry = await resolver.ResolveAsync(
            MakeTicket(TicketState.InProgress),
            WorkingDirectory,
            TargetBranch,
            CancellationToken.None);

        Assert.Equal(StartPhase.ResumeImplement, entry.StartPhase);
        Assert.NotNull(entry.ResumeFeedback);
        Assert.Equal("latest review rationale", entry.ResumeFeedback.Rationale);
        Assert.Equal(new[] { "build", "test" }, entry.ResumeFeedback.ChecksFailed);
        Assert.Equal(1, entry.ResumeFeedback.ReworkRoundNumber);
        Assert.Equal(1, entry.ResumeStartRound);
        Assert.Equal(1, retriever.CallCount);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task ResolveAsync_CommittedWorkWithNullRetriever_SynthesizesResumeFeedback()
    {
        var (resolver, _) = MakeResolver(
            new FakeTicketing(),
            new FakeGit(commitsOnBranch: 1),
            feedbackRetriever: null);

        var entry = await resolver.ResolveAsync(
            MakeTicket(TicketState.InProgress),
            WorkingDirectory,
            TargetBranch,
            CancellationToken.None);

        Assert.Equal(StartPhase.ResumeImplement, entry.StartPhase);
        Assert.Equal(SynthesizedRationale, entry.ResumeFeedback?.Rationale);
        Assert.Empty(entry.ResumeFeedback!.ChecksFailed);
        Assert.Equal(1, entry.ResumeFeedback.ReworkRoundNumber);
        Assert.Equal(1, entry.ResumeStartRound);
    }

    [Fact]
    public async Task ResolveAsync_CommittedWorkWithNoRecoveredResult_SynthesizesResumeFeedback()
    {
        var retriever = new FakeFeedbackRetriever(null);
        var (resolver, _) = MakeResolver(
            new FakeTicketing(),
            new FakeGit(commitsOnBranch: 1),
            retriever);

        var entry = await resolver.ResolveAsync(
            MakeTicket(TicketState.InProgress),
            WorkingDirectory,
            TargetBranch,
            CancellationToken.None);

        Assert.Equal(StartPhase.ResumeImplement, entry.StartPhase);
        Assert.Equal(SynthesizedRationale, entry.ResumeFeedback?.Rationale);
        Assert.Empty(entry.ResumeFeedback!.ChecksFailed);
        Assert.Equal(1, entry.ResumeFeedback.ReworkRoundNumber);
        Assert.Equal(1, retriever.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_PruneFailure_DoesNotAbortFreshImplementResume()
    {
        var ticketing = new FakeTicketing();
        var git = new FakeGit(commitsOnBranch: 0)
        {
            Worktrees = new[]
            {
                new WorktreeInfo(
                    "/repo/.worktrees/tlb-573",
                    "ticket/tlb-573",
                    "abc123",
                    false,
                    false)
            },
            ThrowOnDeleteBranch = true
        };
        var (resolver, sink) = MakeResolver(ticketing, git, feedbackRetriever: null);

        var entry = await resolver.ResolveAsync(
            MakeTicket(TicketState.InProgress),
            WorkingDirectory,
            TargetBranch,
            CancellationToken.None);

        Assert.Equal(StartPhase.Implement, entry.StartPhase);
        Assert.Null(entry.ResumeFeedback);
        Assert.Equal(0, entry.ResumeStartRound);
        Assert.Equal(new[] { (TicketId, TicketState.Ready) }, ticketing.Transitions);
        Assert.Equal(new[] { "/repo/.worktrees/tlb-573" }, git.RemovedWorktrees);
        Assert.Equal(new[] { "ticket/tlb-573" }, git.DeletedBranchesAttempted);

        var transition = Assert.Single(sink.Events);
        Assert.Equal(SessionId, transition.SessionId);
        Assert.Equal(EventKind.StateTransition, transition.Kind);
        Assert.Equal(TicketId, transition.TicketId);
        Assert.Equal(Phase.Chain, transition.Phase);
        Assert.Equal(
            new[] { "from", "reason", "to" },
            transition.Data.Keys.OrderBy(key => key));
        Assert.Equal("InProgress", transition.Data["from"]);
        Assert.Equal("Ready", transition.Data["to"]);
        Assert.Equal("chain_resume", transition.Data["reason"]);
    }

    private static (ChainResumeResolver Resolver, RecordingEventSink Sink) MakeResolver(
        FakeTicketing ticketing,
        FakeGit git,
        IReviewFeedbackRetriever? feedbackRetriever)
    {
        var sink = new RecordingEventSink();
        var emitter = new ChainEventEmitter(sink, ticketing, SessionId);
        return (new ChainResumeResolver(ticketing, git, feedbackRetriever, emitter), sink);
    }

    private static Ticket MakeTicket(TicketState state) =>
        new(
            TicketId,
            "ticket-uuid",
            "Resume ticket",
            "feature",
            state,
            Size.S,
            Risk.Low,
            "<p>description</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);

    private sealed class FakeFeedbackRetriever : IReviewFeedbackRetriever
    {
        private readonly ReviewFeedback? _feedback;

        public FakeFeedbackRetriever(ReviewFeedback? feedback) => _feedback = feedback;

        public int CallCount { get; private set; }

        public ReviewFeedback? GetLatestRework(string ticketId)
        {
            CallCount++;
            return _feedback;
        }
    }

    private sealed class RecordingEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeTicketing : ITicketing
    {
        public List<(string Id, TicketState State)> Transitions { get; } = new();

        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ApplyLabelsAsync(
            string id,
            IEnumerable<string> labels,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddRelationAsync(
            string blockedId,
            string blockerId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(
            string id,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetParentAsync(
            string childUuid,
            string parentUuid,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> QueryAsync(
            TicketQuery query,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionLifecycleAsync(
            string id,
            LifecycleTransition transition,
            string? reason,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid,
            IReadOnlyList<ChildTicketSpec> children,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGit : IGitClient
    {
        private readonly int _commitsOnBranch;

        public FakeGit(int commitsOnBranch) => _commitsOnBranch = commitsOnBranch;

        public IReadOnlyList<WorktreeInfo> Worktrees { get; init; } = Array.Empty<WorktreeInfo>();
        public bool ThrowOnDeleteBranch { get; init; }
        public int RevListCountCallCount { get; private set; }
        public List<string> RemovedWorktrees { get; } = new();
        public List<string> DeletedBranchesAttempted { get; } = new();

        public Task<string> RevParseAsync(
            string refspec,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult("abc123");

        public Task<int> RevListCountAsync(
            string range,
            string workingDirectory,
            CancellationToken ct)
        {
            RevListCountCallCount++;
            return Task.FromResult(_commitsOnBranch);
        }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult(Worktrees);

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path,
            bool force,
            CancellationToken ct)
        {
            RemovedWorktrees.Add(path);
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<GitOpResult> DeleteBranchAsync(
            string branch,
            bool force,
            string mainWorktreePath,
            CancellationToken ct)
        {
            DeletedBranchesAttempted.Add(branch);
            if (ThrowOnDeleteBranch)
                throw new InvalidOperationException("delete failed");
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(
            string pattern,
            string baseBranch,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> DiffAsync(
            string fromRef,
            string toRef,
            string mainWorktreePath,
            bool includePatchContent,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> FetchAsync(
            string remote,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RebaseResult> RebaseAsync(
            string ontoRef,
            string featureWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> RebaseAbortAsync(
            string featureWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> FastForwardMergeAsync(
            string mergeRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> LogOnelineAsync(
            string range,
            int limit,
            string workingDirectory,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
