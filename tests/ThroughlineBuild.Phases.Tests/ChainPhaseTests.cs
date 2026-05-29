using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ChainPhaseTests
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

    private static WorkerResult OkWorkerResult(string? planHtml = null) => new WorkerResult(
        Status.Ok, "ok", Array.Empty<string>(), null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["plan_html"] = planHtml ?? "<p>plan</p>",
            ["risk_label"] = "low",
            ["size_label"] = "s",
            ["planned_at_sha"] = MainSha,
            ["files_changed"] = Array.Empty<string>()
        });

    private static WorkerResult FailWorkerResult(string reason = "worker error") => new WorkerResult(
        Status.Failed, "failed", Array.Empty<string>(), reason,
        new Dictionary<string, object>());

    private int _sessionCounter;
    private string NextSessionId() => $"session-{++_sessionCounter}";

    private ChainPhase BuildChain(
        ChainFakeTicketing ticketing,
        FakeWorkerAgent planWorker,
        FakeWorkerAgent implWorker,
        Queue<IVerifier> verifierQueue,
        FakeWorkerAgent? shipWorker = null,
        FakeGitClientChain? git = null)
    {
        _sessionCounter = 0;
        var events = new FakeEventSinkChain();
        git ??= new FakeGitClientChain();

        var baseOpts = MakeBaseOptions();

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, events, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, ReviewPhase> reviewFactory = opts =>
        {
            var verifier = verifierQueue.Dequeue();
            return new ReviewPhase(ticketing, new FakeWorkerAgent(null), events, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new FakeChecksRunnerChain(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new FakeDecrufterChain(git),
                processPathProvider: () => null);

        return new ChainPhase(
            ticketing, events, baseOpts,
            planFactory, implFactory, reviewFactory, shipFactory,
            sessionIdGenerator: NextSessionId,
            workingDirectory: WorkDir);
    }

    [Fact]
    public async Task RunAsync_HappyPath_BacklogStart_FourSteps_Completed()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal("plan", result.Steps[0].PhaseName);
        Assert.Equal("implement", result.Steps[1].PhaseName);
        Assert.Equal("review", result.Steps[2].PhaseName);
        Assert.Equal("ship", result.Steps[3].PhaseName);
        Assert.All(result.Steps, s => Assert.Equal(Status.Ok, s.Status));
        Assert.Equal(0, result.Steps[1].ReworkRoundNumber);
        Assert.Null(result.FinalRationale);
    }

    [Fact]
    public async Task RunAsync_PlanFails_StoppedAtPlan_OneStep()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(null, fail: true);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtPlan, result.Outcome);
        Assert.Single(result.Steps);
        Assert.Equal("plan", result.Steps[0].PhaseName);
        Assert.Equal(Status.Failed, result.Steps[0].Status);
    }

    [Fact]
    public async Task RunAsync_ImplementInitialFails_StoppedAtImplement_TwoSteps()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(null, fail: true);
        var verifiers = new Queue<IVerifier>();

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtImplement, result.Outcome);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("plan", result.Steps[0].PhaseName);
        Assert.Equal("implement", result.Steps[1].PhaseName);
        Assert.Equal(Status.Failed, result.Steps[1].Status);
    }

    [Fact]
    public async Task RunAsync_ReviewReworkOnceThenPass_SixSteps_Completed()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "missing tests", new[] { "tests_pass" })));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal("plan", result.Steps[0].PhaseName);
        Assert.Equal("implement", result.Steps[1].PhaseName);
        Assert.Equal(0, result.Steps[1].ReworkRoundNumber);
        Assert.Equal("review", result.Steps[2].PhaseName);
        Assert.Equal(VerdictKind.Rework, result.Steps[2].Verdict);
        Assert.Equal("implement", result.Steps[3].PhaseName);
        Assert.Equal(1, result.Steps[3].ReworkRoundNumber);
        Assert.Equal("review", result.Steps[4].PhaseName);
        Assert.Equal(VerdictKind.Pass, result.Steps[4].Verdict);
        Assert.Equal("ship", result.Steps[5].PhaseName);
        Assert.Null(result.FinalRationale);
    }

    [Fact]
    public async Task RunAsync_ReviewReworkTwiceThenPass_EightSteps_Completed()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r1", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r2", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(8, result.Steps.Count);
        Assert.Equal(0, result.Steps[1].ReworkRoundNumber);
        Assert.Equal(1, result.Steps[3].ReworkRoundNumber);
        Assert.Equal(2, result.Steps[5].ReworkRoundNumber);
        Assert.Equal("ship", result.Steps[7].PhaseName);
        Assert.Null(result.FinalRationale);
    }

    [Fact]
    public async Task RunAsync_ReviewReworkThreeTimes_SevenSteps_ReworkCapExceeded_FinalRationalePopulated()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r1", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r2", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r3-cap", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ReworkCapExceeded, result.Outcome);
        Assert.Equal(7, result.Steps.Count);
        Assert.Equal("r3-cap", result.FinalRationale);
        Assert.Equal("review", result.Steps[6].PhaseName);
        Assert.Equal(VerdictKind.Rework, result.Steps[6].Verdict);
        Assert.Empty(verifiers);
    }

    [Fact]
    public async Task RunAsync_ReviewFailFirstCycle_StoppedAtReview_FinalRationalePopulated()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Fail, "fundamental issue", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtReview, result.Outcome);
        Assert.Equal("fundamental issue", result.FinalRationale);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("review", result.Steps[2].PhaseName);
    }

    [Fact]
    public async Task RunAsync_ReviewFailAfterReworkRound1_StoppedAtReview()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "need work", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Fail, "still wrong", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtReview, result.Outcome);
        Assert.Equal("still wrong", result.FinalRationale);
        Assert.Equal(5, result.Steps.Count);
    }

    [Fact]
    public async Task RunAsync_ShipFails_AllPriorPhasesOk_StoppedAtShip()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var git = new FakeGitClientChain(shipFails: true);
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtShip, result.Outcome);
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal("ship", result.Steps[3].PhaseName);
        Assert.Equal(Status.Failed, result.Steps[3].Status);
    }

    [Fact]
    public async Task RunAsync_InitialStateInProgress_RefusedInitialState_ZeroSteps()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.InProgress));
        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(null), new Queue<IVerifier>());

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedInitialState, result.Outcome);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task RunAsync_InitialStateDone_RefusedInitialState_ZeroSteps()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Done));
        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(null), new Queue<IVerifier>());

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedInitialState, result.Outcome);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task RunAsync_InitialStateCancelled_RefusedInitialState_ZeroSteps()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Cancelled));
        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(null), new Queue<IVerifier>());

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedInitialState, result.Outcome);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task RunAsync_InitialStateReady_SkipsPlan_StartsAtImplement()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Ready));
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("implement", result.Steps[0].PhaseName);
        Assert.Equal("review", result.Steps[1].PhaseName);
        Assert.Equal("ship", result.Steps[2].PhaseName);
    }

    [Fact]
    public async Task RunAsync_InitialStateInReview_SkipsPlanAndImplementInitial_StartsAtReview()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedImplementedAt(CommitSha);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(OkWorkerResult().Metadata), verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("review", result.Steps[0].PhaseName);
        Assert.Equal("ship", result.Steps[1].PhaseName);
    }

    [Fact]
    public async Task RunAsync_PerPhaseSessionIdsAreDistinct()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        var sessionIds = result.Steps.Select(s => s.PhaseSessionId).ToList();
        Assert.Equal(sessionIds.Distinct().Count(), sessionIds.Count);
    }

    [Fact]
    public async Task RunAsync_ReworkRoundNumberPropagatesIntoImplementStep()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r1", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "r2", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        var implSteps = result.Steps.Where(s => s.PhaseName == "implement").ToList();
        Assert.Equal(3, implSteps.Count);
        Assert.Equal(0, implSteps[0].ReworkRoundNumber);
        Assert.Equal(1, implSteps[1].ReworkRoundNumber);
        Assert.Equal(2, implSteps[2].ReworkRoundNumber);
    }

    [Fact]
    public async Task RunAsync_ReworkFeedbackPassedToImplement()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata);
        var captureImplWorker = new CapturingImplFactory();
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Rework, "needs-work", new[] { "check_a", "check_b" })));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>())));

        _sessionCounter = 0;
        var events = new FakeEventSinkChain();
        var git = new FakeGitClientChain();

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
        {
            captureImplWorker.Capture(phaseOpts);
            return new ImplementPhase(ticketing, new FakeWorkerAgent(OkWorkerResult().Metadata), events, opts, git, phaseOptions: phaseOpts);
        };

        Func<BuildOptions, ReviewPhase> reviewFactory = opts =>
        {
            var verifier = verifiers.Dequeue();
            return new ReviewPhase(ticketing, new FakeWorkerAgent(null), events, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new FakeChecksRunnerChain(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new FakeDecrufterChain(git),
                processPathProvider: () => null);

        var chain = new ChainPhase(
            ticketing, events, MakeBaseOptions(),
            planFactory, implFactory, reviewFactory, shipFactory,
            sessionIdGenerator: NextSessionId,
            workingDirectory: WorkDir);

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(2, captureImplWorker.CapturedOptions.Count);

        var round0Opts = captureImplWorker.CapturedOptions[0];
        Assert.Null(round0Opts.ReviewFeedback);

        var round1Opts = captureImplWorker.CapturedOptions[1];
        Assert.NotNull(round1Opts.ReviewFeedback);
        Assert.Equal("needs-work", round1Opts.ReviewFeedback!.Rationale);
        Assert.Equal(new[] { "check_a", "check_b" }, round1Opts.ReviewFeedback.ChecksFailed);
        Assert.Equal(1, round1Opts.ReviewFeedback.ReworkRoundNumber);
    }

    private sealed class CapturingImplFactory
    {
        public List<ImplementPhaseOptions> CapturedOptions { get; } = new();
        public void Capture(ImplementPhaseOptions opts) => CapturedOptions.Add(opts);
    }

    private sealed class ChainFakeTicketing : ITicketing
    {
        private Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();

        public ChainFakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedComment(string html) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));

        public void SeedImplementedAt(string sha) =>
            SeedComment($"<p>[implemented_at: {sha}]</p>");

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_ticket);

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
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
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly bool _fail;

        public FakeWorkerAgent(IReadOnlyDictionary<string, object>? metadata, bool fail = false)
        {
            _metadata = metadata;
            _fail = fail;
        }

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

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

    private sealed class FakeEventSinkChain : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeVerifierChain : IVerifier
    {
        private readonly Verdict _verdict;
        public FakeVerifierChain(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(_verdict);
    }

    private sealed class FakeChecksRunnerChain : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public FakeChecksRunnerChain(IReadOnlyList<CheckResult> results) { _results = results; }
        public new Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class FakeDecrufterChain : WorktreeDecrufter
    {
        public FakeDecrufterChain(IGitClient git) : base(git) { }
        public new Task<DecruftResult> DecruftAsync(string featureWorktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }

    private sealed class FakeGitClientChain : IGitClient
    {
        private readonly bool _shipFails;

        public FakeGitClientChain(bool shipFails = false)
        {
            _shipFails = shipFails;
        }

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
            Task.FromResult(_shipFails ? "0000000000000000000000000000000000000000" : CommitSha);

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
        {
            if (_shipFails)
                return Task.FromResult(new RebaseResult(false, false, Array.Empty<string>(), "rebase failed for test"));
            return Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));
        }

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
