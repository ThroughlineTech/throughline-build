using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ChainPhaseEventTests
{
    private const string TicketId = "TLB-1";
    private const string TicketTitle = "Test ticket";
    private const string BranchName = "ticket/tlb-1-test-ticket";
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";
    private static string WorkDir => Directory.GetCurrentDirectory();

    private static Ticket MakeTicket(TicketState state) => new Ticket(
        Id: TicketId,
        Title: TicketTitle,
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeBaseOptions() => new BuildOptions(
        SessionId: "base-session",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static ReviewOptions MakeReviewOptions() => new ReviewOptions(
        Checks: Array.Empty<CheckSpec>(),
        VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null, null));

    private static ShipOptions MakeShipOptions() => new ShipOptions(
        RegressionChecks: Array.Empty<CheckSpec>(),
        Remote: "origin",
        BaseBranch: "main",
        DeleteFeatureBranch: false);

    private static IReadOnlyDictionary<string, object> OkWorkerMeta() => new Dictionary<string, object>
    {
        ["commit_sha"] = CommitSha,
        ["plan_html"] = "<p>plan</p>",
        ["risk_label"] = "low",
        ["size_label"] = "s",
        ["planned_at_sha"] = MainSha,
        ["files_changed"] = Array.Empty<string>()
    };

    private int _sessionCounter;
    private string NextSessionId() => $"session-{++_sessionCounter}";

    private (ChainPhase chain, CapturingEventSink sink) BuildChain(
        EventChainFakeTicketing ticketing,
        EventFakeWorkerAgent planWorker,
        EventFakeWorkerAgent implWorker,
        Queue<IVerifier> verifierQueue,
        EventFakeGitClient? git = null)
    {
        _sessionCounter = 0;
        var sink = new CapturingEventSink();
        git ??= new EventFakeGitClient();

        var baseOpts = MakeBaseOptions();

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, sink, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, sink, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, ReviewPhase> reviewFactory = opts =>
        {
            var verifier = verifierQueue.Dequeue();
            return new ReviewPhase(ticketing, new EventFakeWorkerAgent(null), sink, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, sink, opts, MakeShipOptions(), git,
                checksRunner: new EventFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new EventFakeDecrufter(git),
                processPathProvider: () => null);

        var chain = new ChainPhase(
            ticketing, sink, baseOpts,
            planFactory, implFactory, reviewFactory, shipFactory,
            sessionIdGenerator: NextSessionId,
            workingDirectory: WorkDir);

        return (chain, sink);
    }

    // Test 1: Happy path emits ChainStart, per-phase events, ChainEnd in order; no ReworkRound
    [Fact]
    public async Task HappyPath_EmitsChainStartThenPerPhaseEventsThenChainEnd_NoReworkRound()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var (chain, sink) = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var events = sink.Events;
        Assert.Equal(EventKind.ChainStart, events.First().Kind);
        Assert.Equal(EventKind.ChainEnd, events.Last().Kind);

        var reworkRoundEvents = events.Where(e => e.Kind == EventKind.ReworkRound).ToList();
        Assert.Empty(reworkRoundEvents);

        // ChainStart is first, ChainEnd is last; per-phase events in between
        var chainStartIdx = events.FindIndex(e => e.Kind == EventKind.ChainStart);
        var chainEndIdx = events.FindIndex(e => e.Kind == EventKind.ChainEnd);
        Assert.Equal(0, chainStartIdx);
        Assert.Equal(events.Count - 1, chainEndIdx);

        // All events between ChainStart and ChainEnd are per-phase events (not chain-level)
        var between = events.Skip(1).Take(events.Count - 2).ToList();
        Assert.All(between, e => Assert.NotEqual(EventKind.ChainStart, e.Kind));
        Assert.All(between, e => Assert.NotEqual(EventKind.ChainEnd, e.Kind));
        Assert.All(between, e => Assert.NotEqual(EventKind.ReworkRound, e.Kind));
    }

    // Test 2: One-rework path emits exactly one ReworkRound between first review and second implement
    [Fact]
    public async Task OneRework_EmitsOneReworkRoundEvent_WithCorrectData()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var reworkRationale = "missing tests - this is the rework rationale that should be truncated to 200 chars maximum";
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Rework, reworkRationale, Array.Empty<string>())));
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var (chain, sink) = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var events = sink.Events;
        var reworkEvents = events.Where(e => e.Kind == EventKind.ReworkRound).ToList();
        Assert.Single(reworkEvents);

        var reworkEv = reworkEvents[0];
        Assert.Equal(Phase.Implement, reworkEv.Phase);
        Assert.Equal(1, (int)reworkEv.Data["round"]);
        Assert.Equal("Rework", (string)reworkEv.Data["verdict_that_triggered"]);

        var expectedPreview = reworkRationale.Length <= 200
            ? reworkRationale
            : reworkRationale.Substring(0, 200);
        Assert.Equal(expectedPreview, (string)reworkEv.Data["rationale_preview"]);

        // ReworkRound appears after ChainStart and before ChainEnd
        var chainStartIdx = events.FindIndex(e => e.Kind == EventKind.ChainStart);
        var chainEndIdx = events.FindIndex(e => e.Kind == EventKind.ChainEnd);
        var reworkIdx = events.FindIndex(e => e.Kind == EventKind.ReworkRound);
        Assert.True(reworkIdx > chainStartIdx);
        Assert.True(reworkIdx < chainEndIdx);
    }

    // Test 3: Cap-exceeded path emits exactly two ReworkRound events (rounds 1 and 2), not a third
    [Fact]
    public async Task CapExceeded_EmitsTwoReworkRoundEvents_NotThree_ChainEndOutcomeIsReworkCapExceeded()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Rework, "r1", Array.Empty<string>())));
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Rework, "r2", Array.Empty<string>())));
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Rework, "r3-cap", Array.Empty<string>())));

        var (chain, sink) = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ReworkCapExceeded, result.Outcome);

        var events = sink.Events;
        var reworkEvents = events.Where(e => e.Kind == EventKind.ReworkRound).ToList();
        Assert.Equal(2, reworkEvents.Count);
        Assert.Equal(1, (int)reworkEvents[0].Data["round"]);
        Assert.Equal(2, (int)reworkEvents[1].Data["round"]);

        var chainEndEv = events.Last();
        Assert.Equal(EventKind.ChainEnd, chainEndEv.Kind);
        Assert.Equal("ReworkCapExceeded", (string)chainEndEv.Data["outcome"]);
        Assert.Equal(2, (int)chainEndEv.Data["rework_rounds"]);
        Assert.Equal(result.Steps.Count, (int)chainEndEv.Data["phases_run"]);
    }

    // Test 4: ChainEnd Data has correct outcome/phases_run/rework_rounds for the happy path
    [Fact]
    public async Task HappyPath_ChainEndData_HasCorrectOutcomeAndCounts()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var (chain, sink) = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var chainEndEv = sink.Events.Last();
        Assert.Equal(EventKind.ChainEnd, chainEndEv.Kind);
        Assert.Equal("Completed", (string)chainEndEv.Data["outcome"]);
        Assert.Equal(4, (int)chainEndEv.Data["phases_run"]);
        Assert.Equal(0, (int)chainEndEv.Data["rework_rounds"]);
        Assert.False(chainEndEv.Data.ContainsKey("final_rationale_preview"));
        Assert.True((long)chainEndEv.Data["total_duration_ms"] >= 0);
    }

    // Test 5: RefusedInitialState emits ChainStart then ChainEnd with phases_run=0, no other events between
    [Fact]
    public async Task RefusedInitialState_EmitsChainStartAndChainEnd_NothingBetween_PhasesRunZero()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.InProgress));
        var (chain, sink) = BuildChain(
            ticketing,
            new EventFakeWorkerAgent(null),
            new EventFakeWorkerAgent(null),
            new Queue<IVerifier>());

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedInitialState, result.Outcome);

        var events = sink.Events;
        Assert.Equal(2, events.Count);
        Assert.Equal(EventKind.ChainStart, events[0].Kind);
        Assert.Equal(EventKind.ChainEnd, events[1].Kind);

        var chainEndEv = events[1];
        Assert.Equal("RefusedInitialState", (string)chainEndEv.Data["outcome"]);
        Assert.Equal(0, (int)chainEndEv.Data["phases_run"]);
        Assert.Equal(0, (int)chainEndEv.Data["rework_rounds"]);
    }

    // CapturingEventSink records all emitted events in order
    private sealed class CapturingEventSink : IEventSink
    {
        private readonly List<WorkflowEvent> _events = new();
        public List<WorkflowEvent> Events => _events;

        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            _events.Add(ev);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EventChainFakeTicketing : ITicketing
    {
        private Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();

        public EventChainFakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedImplementedAt(string sha) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(),
                $"<p>[implemented_at: {sha}]</p>", DateTimeOffset.UtcNow));

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_ticket);

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            _ticket = _ticket with { State = newState };
            if (newState == TicketState.InReview)
            {
                _seededComments.Add(new TicketComment(
                    Guid.NewGuid().ToString(),
                    $"<p>[implemented_at: {CommitSha}]</p>",
                    DateTimeOffset.UtcNow));
            }
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));
            return Task.FromResult("comment-id");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(_seededComments.ToList());

        public Task<NewTicketResult> CreateTicketAsync(
            string title, string type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class EventFakeWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly bool _fail;

        public EventFakeWorkerAgent(IReadOnlyDictionary<string, object>? metadata, bool fail = false)
        {
            _metadata = metadata;
            _fail = fail;
        }

        public string Name => "fake";

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            if (_fail)
                return Task.FromResult(new WorkerResult(
                    Status.Failed, "failed", Array.Empty<string>(), "worker error",
                    new Dictionary<string, object>()));
            return Task.FromResult(new WorkerResult(
                Status.Ok, "ok", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>()));
        }
    }

    private sealed class EventFakeVerifier : IVerifier
    {
        private readonly Verdict _verdict;
        public EventFakeVerifier(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(_verdict);
    }

    private sealed class EventFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public EventFakeChecksRunner(IReadOnlyList<CheckResult> results) { _results = results; }
        public new Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class EventFakeDecrufter : WorktreeDecrufter
    {
        public EventFakeDecrufter(IGitClient git) : base(git) { }
        public new Task<DecruftResult> DecruftAsync(string featureWorktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }

    private sealed class EventFakeGitClient : IGitClient
    {
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(new[]
            {
                new WorktreeInfo("/fake/worktree", BranchName, CommitSha, false, false)
            });

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(CommitSha);

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

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
