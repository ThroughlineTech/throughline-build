using System.Text.Json;
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
    private static string WorkDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static Ticket MakeTicket(TicketState state) => new Ticket(
        Id: TicketId,
        Uuid: "ticket-uuid-1",
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

    private static WorkerResult OkWorkerResult(string? planMarkdown = null) => new WorkerResult(
        Status.Ok, "ok", Array.Empty<string>(), null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "s",
            ["planned_at_sha"] = MainSha,
            ["files_changed"] = Array.Empty<string>()
        },
        Blocks: new Dictionary<string, string>
        {
            ["PLAN_BODY"] = planMarkdown ?? "# Plan\nThis is the plan."
        });

    private static WorkerResult FailWorkerResult(string reason = "worker error") => new WorkerResult(
        Status.Failed, "failed", Array.Empty<string>(), reason,
        new Dictionary<string, object>());

    private int _sessionCounter;
    private string NextSessionId() => $"session-{++_sessionCounter}";

    private ChainPhase BuildChain(
        ChainFakeTicketing ticketing,
        IWorkerAgent planWorker,
        IWorkerAgent implWorker,
        Queue<IVerifier> verifierQueue,
        IWorkerAgent? shipWorker = null,
        FakeGitClientChain? git = null,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory = null,
        bool forwardGitToChain = false)
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
            workingDirectory: WorkDir,
            ratifierFactory: ratifierFactory,
            gitClient: forwardGitToChain ? git : null);
    }

    [Fact]
    public async Task RunAsync_HappyPath_BacklogStart_FourSteps_Completed()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks), verifiers);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
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
            return new ImplementPhase(ticketing, new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks), events, opts, git, phaseOptions: phaseOpts);
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
        private readonly Dictionary<string, Ticket> _extraTickets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Ticket>> _childrenByParentUuid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Relation>> _relationsByTicketId = new(StringComparer.Ordinal);
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> PostedComments { get; } = new();
        public int GetRelationsCallCount { get; private set; }

        public ChainFakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedComment(string html) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));

        public void SeedImplementedAt(string sha) =>
            SeedComment($"<p>[implemented_at: {sha}]</p>");

        /// <summary>Seed an additional ticket lookup by its Id (e.g. "TLB-2").</summary>
        public void SeedTicket(Ticket t) => _extraTickets[t.Id] = t;

        /// <summary>Seed children returned for a given parent UUID.</summary>
        public void SeedChildren(string parentUuid, IReadOnlyList<Ticket> children)
        {
            _childrenByParentUuid[parentUuid] = children.ToList();
            foreach (var c in children)
                _extraTickets[c.Id] = c;
        }

        /// <summary>Seed relations returned for a given ticket ID.</summary>
        public void SeedRelations(string ticketId, IReadOnlyList<Relation> rels) =>
            _relationsByTicketId[ticketId] = rels.ToList();

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct)
        {
            if (_extraTickets.TryGetValue(id, out var extra))
                return Task.FromResult(extra);
            return Task.FromResult(_ticket);
        }

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            if (_extraTickets.TryGetValue(id, out var extra))
            {
                var updated = extra with { State = newState };
                _extraTickets[id] = updated;
                if (newState == TicketState.InReview)
                {
                    _seededComments.Add(new TicketComment(
                        Guid.NewGuid().ToString(),
                        $"<p>[implemented_at: {CommitSha}]</p>",
                        DateTimeOffset.UtcNow));
                }
                return Task.CompletedTask;
            }
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
            PostedComments.Add((id, html));
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));
            return Task.FromResult("comment-id");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
        {
            GetRelationsCallCount++;
            if (_relationsByTicketId.TryGetValue(id, out var rels))
                return Task.FromResult<IReadOnlyList<Relation>>(rels.AsReadOnly());
            return Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());
        }

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

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct)
        {
            if (query.ParentId is not null &&
                _childrenByParentUuid.TryGetValue(query.ParentId, out var kids))
                return Task.FromResult<IReadOnlyList<Ticket>>(kids.AsReadOnly());
            return Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        }

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
    
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
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;
        private readonly bool _fail;

        public FakeWorkerAgent(IReadOnlyDictionary<string, object>? metadata, bool fail = false,
            IReadOnlyDictionary<string, string>? blocks = null)
        {
            _metadata = metadata;
            _blocks = blocks;
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
                _metadata ?? new Dictionary<string, object>(), _blocks));
        }
    }

    /// <summary>Worker that fails on the first call and succeeds on all subsequent calls.</summary>
    private sealed class FailFirstWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object> _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;
        private int _callCount;

        public FailFirstWorkerAgent(IReadOnlyDictionary<string, object> metadata,
            IReadOnlyDictionary<string, string>? blocks = null)
        {
            _metadata = metadata;
            _blocks = blocks;
        }

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            _callCount++;
            if (_callCount == 1)
                return Task.FromResult(new WorkerResult(
                    Status.Failed, "failed", Array.Empty<string>(), "worker error",
                    new Dictionary<string, object>()));
            return Task.FromResult(new WorkerResult(
                Status.Ok, "ok", Array.Empty<string>(), null, _metadata, _blocks));
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

    // -------------------------------------------------------------------------
    // Ratification test helpers
    // -------------------------------------------------------------------------

    private static WorkerResult ObsoleteEscalateWorkerResult(string commit = "abc123obsolete") =>
        new WorkerResult(
            Status.Escalate,
            "ticket is obsolete",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>
            {
                ["escalation"] = JsonSerializer.Deserialize<JsonElement>($$"""
                    {
                      "reason": "obsolete",
                      "subsumed_by": {
                        "commit": "{{commit}}",
                        "files": ["src/Foo.cs"],
                        "rationale": "already done in prior commit"
                      }
                    }
                    """)
            });

    private static WorkerResult NonObsoleteEscalateWorkerResult() =>
        new WorkerResult(
            Status.Escalate,
            "ticket is blocked",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>
            {
                ["escalation"] = JsonSerializer.Deserialize<JsonElement>("""{"reason":"blocked"}""")
            });

    private sealed class EscalateWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public EscalateWorkerAgent(WorkerResult result) { _result = result; }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    private sealed class FakeObsoleteRatifier : IObsoleteRatifier
    {
        private readonly Verdict _verdict;
        public bool WasCalled { get; private set; }
        public FakeObsoleteRatifier(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(_verdict);
        }
    }

    // -------------------------------------------------------------------------
    // Ratification tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_PlanObsoleteEscalation_RatifiedByRatifier_RatifiedObsoleteOutcome_SubsumedByCarried()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EscalateWorkerAgent(ObsoleteEscalateWorkerResult("abc123def"));
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Pass, "prior work satisfies acceptance criteria", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RatifiedObsolete, result.Outcome);
        Assert.True(ratifier.WasCalled);
        Assert.NotNull(result.SubsumedBy);
        Assert.Equal("abc123def", result.SubsumedBy.Commit);
        // Steps: plan + ratify
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("plan", result.Steps[0].PhaseName);
        Assert.Equal("ratify", result.Steps[1].PhaseName);
        Assert.Equal(Status.Ok, result.Steps[1].Status);
        // Done transition and rationale comment
        Assert.Contains(ticketing.Transitions, t => t.state == TicketState.Done);
        Assert.NotNull(result.FinalRationale);
        Assert.Contains("abc123def", result.FinalRationale);
    }

    [Fact]
    public async Task RunAsync_PlanObsoleteEscalation_RejectedByRatifier_StoppedAtPlan()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EscalateWorkerAgent(ObsoleteEscalateWorkerResult());
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Fail, "acceptance criteria not satisfied", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtPlan, result.Outcome);
        Assert.True(ratifier.WasCalled);
        Assert.Null(result.SubsumedBy);
    }

    [Fact]
    public async Task RunAsync_ImplementObsoleteEscalation_RatifiedByRatifier_RatifiedObsoleteOutcome()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new EscalateWorkerAgent(ObsoleteEscalateWorkerResult("deadbeef"));
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Pass, "prior work satisfies acceptance criteria", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RatifiedObsolete, result.Outcome);
        Assert.True(ratifier.WasCalled);
        Assert.NotNull(result.SubsumedBy);
        Assert.Equal("deadbeef", result.SubsumedBy.Commit);
        // Steps: plan + implement + ratify
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("ratify", result.Steps[2].PhaseName);
        Assert.Equal(Status.Ok, result.Steps[2].Status);
        // Done transition and rationale comment
        Assert.Contains(ticketing.Transitions, t => t.state == TicketState.Done);
        Assert.NotNull(result.FinalRationale);
        Assert.Contains("deadbeef", result.FinalRationale);
    }

    [Fact]
    public async Task RunAsync_ImplementObsoleteEscalation_RejectedByRatifier_StoppedAtImplement()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new EscalateWorkerAgent(ObsoleteEscalateWorkerResult());
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Fail, "acceptance criteria not satisfied", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtImplement, result.Outcome);
        Assert.True(ratifier.WasCalled);
        Assert.Null(result.SubsumedBy);
    }

    [Fact]
    public async Task RunAsync_NoAutoResolve_SkipsRatifier_StoppedAtPlan()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EscalateWorkerAgent(ObsoleteEscalateWorkerResult());
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Pass, "should not be called", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, null, NoAutoResolve: true), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtPlan, result.Outcome);
        Assert.False(ratifier.WasCalled);
        Assert.DoesNotContain(ticketing.Transitions, t => t.state == TicketState.Done);
    }

    [Fact]
    public async Task RunAsync_NonObsoleteEscalation_RatifierNotCalled_StoppedAtPlan()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EscalateWorkerAgent(NonObsoleteEscalateWorkerResult());
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var ratifier = new FakeObsoleteRatifier(
            new Verdict(VerdictKind.Pass, "should not be called", Array.Empty<string>()));
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory = _ => ratifier;

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, ratifierFactory: ratifierFactory);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.StoppedAtPlan, result.Outcome);
        Assert.False(ratifier.WasCalled);
        Assert.Null(result.SubsumedBy);
    }

    // -------------------------------------------------------------------------
    // Parent-chain tests
    // -------------------------------------------------------------------------

    private static Ticket MakeChildTicket(string id, string uuid, TicketState state) => new Ticket(
        Id: id,
        Uuid: uuid,
        Title: $"Child ticket {id}",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>child</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: "ticket-uuid-1");

    [Fact]
    public async Task RunAsync_ParentWith2BacklogChildren_BothComplete_ParentCompleted_TwoChildResults()
    {
        // Parent ticket has 2 Backlog children; both should complete.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // 2 children * 1 review pass each
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    [Fact]
    public async Task RunAsync_ParentWhoseChildHasItsOwnChildren_StopsWithParentHasGrandchildren_RunsNothing()
    {
        // Operation -> plan -> brief: a 3-level tree. Chaining the operation must NOT
        // recurse into the grandchildren (that was the infinite-recursion bug). It
        // stops and tells the operator to chain the intermediate plan directly.
        var parent = MakeTicket(TicketState.Backlog);                              // uuid ticket-uuid-1
        var plan = MakeChildTicket("TLB-2", "plan-uuid", TicketState.Backlog);     // child of parent
        var brief = MakeChildTicket("TLB-3", "brief-uuid", TicketState.Backlog);   // child of plan

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { plan });   // parent -> plan
        ticketing.SeedChildren("plan-uuid", new[] { brief });      // plan -> brief (grandchild)

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(null), new Queue<IVerifier>());
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentHasGrandchildren, result.Outcome);
        Assert.Contains("TLB-2", result.FinalRationale);
        Assert.Null(result.ChildResults);
        // Nothing ran: no phase transitions, no comments posted.
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ParentWithOneDoneChildAndOneBacklogChild_SkipsDone_OneChildResult_ParentCompleted()
    {
        // Parent has 1 Done child (skipped) and 1 Backlog child (processed).
        var parent = MakeTicket(TicketState.Backlog);
        var doneChild = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Done);
        var activeChild = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { doneChild, activeChild });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // Only 1 active child needs a review
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        // Only the Backlog child was processed
        var onlyChild = Assert.Single(result.ChildResults!);
        Assert.Equal("TLB-3", onlyChild.TicketId);
        Assert.Equal(ChainOutcome.Completed, onlyChild.Outcome);
    }

    [Fact]
    public async Task RunAsync_ParentWithAllDoneChildren_NoEligible_ParentCompleted_ZeroChildResults()
    {
        // Parent has only Done children; no eligible children to process.
        var parent = MakeTicket(TicketState.Backlog);
        var done1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Done);
        var done2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Cancelled);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { done1, done2 });

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), new FakeWorkerAgent(null), new Queue<IVerifier>());
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Empty(result.ChildResults!);
    }

    [Fact]
    public async Task RunAsync_ParentWith2BacklogChildren_OneChildFailsPlan_ParentStoppedEarly_TwoChildResults()
    {
        // Parent ticket has 2 Backlog children; first child fails at plan (StoppedAtPlan),
        // second child succeeds. anyStoppedEarly should be true -> ParentStoppedEarly.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        // Plan worker fails on first call (child1), succeeds on subsequent calls (child2).
        var planWorker = new FailFirstWorkerAgent(OkWorkerResult().Metadata, OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // Only child2 reaches review
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.Contains(result.ChildResults, r => r.Outcome != ChainOutcome.Completed);
    }

    // -------------------------------------------------------------------------
    // Sibling dep-analysis tests (TLB-329)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ParentWithDependentChildren_DepRespected_SecondRunsAfterFirst()
    {
        // child-2 (TLB-3) is blocked_by child-1 (TLB-2).
        // Level analysis must produce [[TLB-2], [TLB-3]]; TLB-2 must appear first in ChildResults.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });
        // TLB-3 is blocked_by TLB-2 -> TLB-2 must run first
        ticketing.SeedRelations("TLB-3", new[] { new Relation("blocked_by", "TLB-2") });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // 2 children * 1 review pass each
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        // Both complete
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
        // Level ordering: TLB-2 (level 0) appears before TLB-3 (level 1)
        Assert.Equal("TLB-2", result.ChildResults[0].TicketId);
        Assert.Equal("TLB-3", result.ChildResults[1].TicketId);
        // Dep analysis ran (GetRelationsAsync called for each eligible child)
        Assert.Equal(2, ticketing.GetRelationsCallCount);
    }

    [Fact]
    public async Task RunAsync_ParentWithIndependentChildren_BothRunConcurrently_NoDepsRequired()
    {
        // Two siblings with no relations: both land in the same level. With width-1 dispatch
        // they run sequentially within that level, but both complete (same outcome as before).
        // Regression guard: behavior identical to pre-TLB-329 fan-out when no deps exist.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });
        // No relations seeded -> both children independent

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
        // Dep analysis still ran even though no deps were found
        Assert.Equal(2, ticketing.GetRelationsCallCount);
    }

    [Fact]
    public async Task RunAsync_DirtyTree_UnrelatedStash_RefusesBeforeAnyPhase()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // Repo-global stash from unrelated work on main - does not mention ticket/tlb-1-.
        var git = new FakeGitClientChain(stashEntries: new[]
        {
            "stash@{0}: WIP on main: 06a1156 TLB-343: document decompose decision"
        });

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedDirtyTree, result.Outcome);
        Assert.Contains("dangling stash", result.FinalRationale);
        // Refused before planning: no phase steps were recorded and the ticket never moved.
        Assert.Empty(result.Steps);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_StashForThisTicket_DoesNotRefuse()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        // A stash that mentions this ticket's branch prefix is treated as related, not dangling.
        var git = new FakeGitClientChain(stashEntries: new[]
        {
            $"stash@{{0}}: On {BranchName}: in-progress work"
        });

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.NotEqual(ChainOutcome.RefusedDirtyTree, result.Outcome);
    }

    private sealed class FakeGitClientChain : IGitClient
    {
        private readonly bool _shipFails;
        private readonly List<WorktreeInfo> _worktrees = new();
        private readonly IReadOnlyList<string> _stashEntries;

        public FakeGitClientChain(bool shipFails = false, IReadOnlyList<string>? stashEntries = null)
        {
            _shipFails = shipFails;
            _stashEntries = stashEntries ?? Array.Empty<string>();
            // Seed the default single-ticket worktree for backward compatibility.
            _worktrees.Add(new WorktreeInfo("/fake/worktree", BranchName, CommitSha, false, false));
        }

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_stashEntries);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            Directory.CreateDirectory(worktreePath);
            // Track the created worktree so ListWorktreesAsync can find it during ship.
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, CommitSha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

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
