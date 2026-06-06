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
        bool forwardGitToChain = false,
        BuildOptions? baseOptions = null,
        FakeEventSinkChain? eventSink = null,
        IWorkerAgent? batchWorker = null)
    {
        _sessionCounter = 0;
        var events = eventSink ?? new FakeEventSinkChain();
        git ??= new FakeGitClientChain();

        var baseOpts = baseOptions ?? MakeBaseOptions();

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
            gitClient: forwardGitToChain ? git : null,
            batchWorker: batchWorker);
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
    public async Task RunAsync_HappyPath_EmitsStartMarkerBeforeEachPhaseCompletion()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var emitted = new List<ChainStep>();
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, (_, step) => emitted.Add(step)),
            CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        // Each of the four phases emits a START marker before its completion step.
        foreach (var phase in new[] { "plan", "implement", "review", "ship" })
        {
            var startIdx = emitted.FindIndex(s => s.PhaseName == phase && s.IsStart);
            var doneIdx = emitted.FindIndex(s => s.PhaseName == phase && !s.IsStart);
            Assert.True(startIdx >= 0, $"missing START marker for {phase}");
            Assert.True(doneIdx > startIdx, $"START marker for {phase} must precede its completion");
        }
        // Start markers are console-only: never added to the returned ChainResult.
        Assert.DoesNotContain(result.Steps, s => s.IsStart);
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
    public async Task RunAsync_InitialStateInProgress_NoCommits_ResetsToReady_ResumesAtImplement_Completed()
    {
        // Interrupted *initial* implement: Ready->InProgress fired but the worker never committed,
        // so the branch carries no work. The chain prunes the orphan and restarts implement cleanly.
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.InProgress));
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        // Reset InProgress -> Ready happened before the implement round.
        Assert.Contains(ticketing.Transitions, t => t.state == TicketState.Ready);
        // Fresh implement (round 0) -> review -> ship; no plan (already past Backlog).
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("implement", result.Steps[0].PhaseName);
        Assert.Equal(0, result.Steps[0].ReworkRoundNumber);
        Assert.Equal("review", result.Steps[1].PhaseName);
        Assert.Equal("ship", result.Steps[2].PhaseName);
    }

    [Fact]
    public async Task RunAsync_InitialStateInProgress_WithCommits_ResumesAsReworkRound_Completed()
    {
        // In-progress branch carries real work (interrupted rework): resume in place via the rework
        // path (round 1, reuses the worktree) - no reset to Ready, no prune.
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.InProgress));
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        var git = new FakeGitClientChain(revListCount: 1);

        var chain = BuildChain(ticketing, new FakeWorkerAgent(null), implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        // No reset-to-Ready and no prune: resumed in place.
        Assert.DoesNotContain(ticketing.Transitions, t => t.state == TicketState.Ready);
        Assert.Empty(git.DeletedBranches);
        // Implement resumes as rework round 1 -> review -> ship.
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("implement", result.Steps[0].PhaseName);
        Assert.Equal(1, result.Steps[0].ReworkRoundNumber);
        Assert.Equal("review", result.Steps[1].PhaseName);
        Assert.Equal("ship", result.Steps[2].PhaseName);
    }

    [Fact]
    public async Task RunAsync_InitialStatePlanning_ResetsToBacklog_ResumesAtPlan_Completed()
    {
        // Plan started but never finished (stuck in Planning): reset to Backlog and replan from scratch.
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Planning));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Contains(ticketing.Transitions, t => t.state == TicketState.Backlog);
        // Full plan -> implement -> review -> ship.
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal("plan", result.Steps[0].PhaseName);
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
        public List<WorkerOptions> SeenOptions { get; } = new();

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            SeenOptions.Add(options);
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

    // Returns a configurable WorkerResult (with optional per-ticket Tickets array) and
    // tracks how many times ExecuteAsync was called so tests can assert call count.
    private sealed class BatchFakeWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyList<BatchTicketResult>? _tickets;
        private readonly Status _status;
        public int CallCount { get; private set; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public BatchFakeWorkerAgent(
            IReadOnlyList<BatchTicketResult>? tickets = null,
            Status status = Status.Ok)
        {
            _tickets = tickets;
            _status = status;
        }

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            CallCount++;
            // Include verdict=Pass so combined-review calls return a valid passing verdict.
            // The implement call ignores the verdict key, so this is safe for both phases.
            var metadata = _status == Status.Ok
                ? new Dictionary<string, object> { ["verdict"] = "Pass", ["rationale"] = "looks good" }
                : new Dictionary<string, object>();
            return Task.FromResult(new WorkerResult(
                _status,
                _status == Status.Ok ? "batch ok" : "batch failed",
                Array.Empty<string>(),
                _status != Status.Ok ? "batch error" : null,
                metadata,
                Tickets: _tickets));
        }
    }

    private sealed class FakeEventSinkChain : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }

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
        public string? LastEvidenceDirectory { get; private set; }
        public FakeObsoleteRatifier(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, string? evidenceDirectory, CancellationToken ct)
        {
            WasCalled = true;
            LastEvidenceDirectory = evidenceDirectory;
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
    public async Task RunAsync_ParentWithUnorderedChildren_DispatchesLowestTicketNumberFirst()
    {
        // Two unordered children (no blocked_by edge) seeded highest-number-first.
        // Use TLB-10 and TLB-2 to prove numeric ordering, not lexicographic (where "10" < "2").
        var parent = MakeTicket(TicketState.Backlog);
        var high = MakeChildTicket("TLB-10", "child-uuid-high", TicketState.Backlog);
        var low = MakeChildTicket("TLB-2", "child-uuid-low", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { high, low });

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
        // ChildResults preserve dispatch order: lowest number first despite reverse seed order.
        Assert.Equal("TLB-2", result.ChildResults![0].TicketId);
        Assert.Equal("TLB-10", result.ChildResults![1].TicketId);
    }

    [Fact]
    public async Task RunAsync_ParentWhoseChildHasItsOwnChildren_RunsGrandchildThenRollsUpParents()
    {
        // Operation -> plan -> brief: a 3-level tree. Chaining the operation now
        // recurses post-order: run the brief leaf, roll up the plan, then roll up
        // the operation.
        var parent = MakeTicket(TicketState.Backlog);                              // uuid ticket-uuid-1
        var plan = MakeChildTicket("TLB-2", "plan-uuid", TicketState.Backlog);     // child of parent
        var brief = MakeChildTicket("TLB-3", "brief-uuid", TicketState.Backlog);   // child of plan

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { plan });   // parent -> plan
        ticketing.SeedChildren("plan-uuid", new[] { brief });      // plan -> brief (grandchild)

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        var planResult = Assert.Single(result.ChildResults!);
        Assert.Equal("TLB-2", planResult.TicketId);
        Assert.Equal(ChainOutcome.ParentCompleted, planResult.Outcome);
        var briefResult = Assert.Single(planResult.ChildResults!);
        Assert.Equal("TLB-3", briefResult.TicketId);
        Assert.Equal(ChainOutcome.Completed, briefResult.Outcome);
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
    public async Task RunAsync_ParentWith2BacklogChildren_OneChildFailsPlan_ParentStoppedEarly_SecondChildNotRun()
    {
        // Parent ticket has 2 Backlog children; first child fails at plan (StoppedAtPlan).
        // The second sibling must not run from the stale pre-child1 base.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        // Plan worker fails on first call (child1), succeeds on subsequent calls if reached.
        var planWorker = new FailFirstWorkerAgent(OkWorkerResult().Metadata, OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        var only = Assert.Single(result.ChildResults!);
        Assert.Equal("TLB-2", only.TicketId);
        Assert.NotEqual(ChainOutcome.Completed, only.Outcome);
    }

    [Fact]
    public async Task RunAsync_ParentWithInProgressChild_ResumesChild_ParentCompleted()
    {
        // Regression for the TLB-368 failure: a child left InProgress by an interrupted run was
        // "eligible" (not Done/Cancelled) but the router refused it, flipping the whole parent to
        // ParentStoppedEarly. The chain must now resume the InProgress child instead of refusing.
        var parent = MakeTicket(TicketState.Backlog);
        var inProgressChild = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.InProgress);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { inProgressChild });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        var only = Assert.Single(result.ChildResults!);
        Assert.Equal("TLB-2", only.TicketId);
        Assert.Equal(ChainOutcome.Completed, only.Outcome);
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
    public async Task RunAsync_ParentWithIndependentChildren_BothRunSequentially_NoDepsRequired()
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
        Assert.Equal(DirtyTreeCause.Hygiene, result.DirtyTreeCause);
        Assert.Contains("dangling stash", result.FinalRationale);
        // Refused before planning: no phase steps were recorded and the ticket never moved.
        Assert.Empty(result.Steps);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_CleanMainPreflight_AllowsChainToProceed()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        var git = new FakeGitClientChain(trackedChanges: Array.Empty<string>());

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.Equal(4, result.Steps.Count);
        Assert.True(git.GetTrackedChangesCallCount >= 1);
    }

    [Fact]
    public async Task RunAsync_DirtyTrackedMain_RefusesBeforeAnyPhaseAndEmitsGateFailure()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        var git = new FakeGitClientChain(trackedChanges: new[] { "src/Dirty.cs", "docs/dirty.md" });
        var events = new FakeEventSinkChain();

        var chain = BuildChain(
            ticketing,
            planWorker,
            implWorker,
            verifiers,
            git: git,
            forwardGitToChain: true,
            eventSink: events);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedDirtyTree, result.Outcome);
        Assert.Equal(DirtyTreeCause.TrackedChanges, result.DirtyTreeCause);
        Assert.Contains("modified tracked files", result.FinalRationale);
        Assert.Contains("src/Dirty.cs", result.FinalRationale);
        Assert.Empty(result.Steps);
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(planWorker.SeenOptions);

        var gate = Assert.Single(events.Events.Where(e => e.Kind == EventKind.GateFailure));
        Assert.Equal(Phase.Chain, gate.Phase);
        Assert.Equal("chain_preflight_dirty", gate.Data["kind"].ToString());
        Assert.Equal(2, Convert.ToInt32(gate.Data["dirty_count"]));
        Assert.Equal(git.WorkingDirectoriesSeenForTrackedChanges[0], gate.Data["worktree"].ToString());
        var dirtyPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(gate.Data["dirty_paths"]);
        Assert.Equal(new[] { "src/Dirty.cs", "docs/dirty.md" }, dirtyPaths);

        Assert.Contains(events.Events, e => e.Kind == EventKind.ChainEnd
            && e.Data["outcome"].ToString() == ChainOutcome.RefusedDirtyTree.ToString());
    }

    [Fact]
    public async Task RunAsync_WrongBranchMain_RefusesBeforeAnyPhaseAndEmitsGateFailure()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // Main worktree parked on a feature branch while the chain targets "main": the ship
        // gate would refuse, so the preflight guard must refuse first, before any phase runs.
        var git = new FakeGitClientChain(currentBranch: "some-feature-branch");
        var events = new FakeEventSinkChain();

        var chain = BuildChain(
            ticketing,
            planWorker,
            implWorker,
            verifiers,
            git: git,
            forwardGitToChain: true,
            eventSink: events);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RefusedWrongBranch, result.Outcome);
        Assert.Contains("some-feature-branch", result.FinalRationale);
        Assert.Contains("must be on 'main'", result.FinalRationale);
        Assert.Empty(result.Steps);
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(planWorker.SeenOptions);

        var gate = Assert.Single(events.Events.Where(e => e.Kind == EventKind.GateFailure));
        Assert.Equal(Phase.Chain, gate.Phase);
        Assert.Equal("chain_preflight_wrong_branch", gate.Data["kind"].ToString());
        Assert.Equal("main", gate.Data["expected"].ToString());
        Assert.Equal("some-feature-branch", gate.Data["actual"].ToString());

        Assert.Contains(events.Events, e => e.Kind == EventKind.ChainEnd
            && e.Data["outcome"].ToString() == ChainOutcome.RefusedWrongBranch.ToString());
    }

    [Fact]
    public async Task RunAsync_UntrackedOnlyMain_DoesNotRefuse()
    {
        var ticketing = new ChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        // GetTrackedChangesAsync mirrors ShipPhase's tracked-only policy; untracked-only
        // status returns no entries and therefore must not block chain preflight.
        var git = new FakeGitClientChain(trackedChanges: Array.Empty<string>());

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.NotEqual(ChainOutcome.RefusedDirtyTree, result.Outcome);
        Assert.Equal(ChainOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task RunAsync_ParentChain_TrackedDirtyGateRunsOnlyAtOutermostInvocation()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);
        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        var git = new FakeGitClientChain(trackedChanges: Array.Empty<string>());

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git, forwardGitToChain: true);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.Equal(1, git.GetTrackedChangesCallsBeforeSharedWorktreeCreation);
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

    // -------------------------------------------------------------------------
    // TLB-402: main-worktree detached-HEAD regression (chain-348 scenario)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ParentChain_B02FailsForFirstChild_AbortsAndStopsBeforeSecondChild()
    {
        // Regression for TLB-402: when B02 auto-rebase fails without conflict markers
        // (e.g., hook rejection), the conditional abort was skipped (HadConflicts=false),
        // leaving the main worktree's HEAD detached. The second child's ship then operated
        // on a detached HEAD, causing silent data corruption.
        // Fix (TLB-402): abort is now unconditional. Parent-chain stacking also means child 2
        // is not dispatched after child 1 fails to ship.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // child1 passes review, then fails at ship (B02 abort); child2 must not reach review.
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var git = new FakeGitClientChain()
        {
            // Make both ancestry checks return false -> diverged path -> B02 triggered
            TriggerDivergence = true,
            DivergenceStateResult = DivergenceState.DivergedNoConflict,
            // Response queue:
            //   call 1: child1 B02 auto-rebase fails without conflict markers (hook rejection)
            RebaseResponses = new Queue<RebaseResult>(new[]
            {
                new RebaseResult(false, false, Array.Empty<string>(), "hook-rejected"),
            })
        };

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, git: git);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        // child1 failed at ship; child2 was never run -> ParentStoppedEarly
        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        var child1Result = Assert.Single(result.ChildResults!);
        Assert.Equal("TLB-2", child1Result.TicketId);
        Assert.NotEqual(ChainOutcome.Completed, child1Result.Outcome);

        // Abort was called after the non-conflict B02 failure (Fix 1)
        Assert.Equal(1, git.RebaseAbortCallCount);
    }

    // -------------------------------------------------------------------------
    // TLB-403: child step attribution test
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ParentChain_OnStep_ChildStepsCarryChildId_NotParentId()
    {
        // Verify that when OnStep is invoked for a child ticket's phases, the ticket ID
        // passed to the callback is the child's ID, not the parent's.
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var capturedSteps = new List<(string id, string phase)>();

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var options = new ChainPhaseOptions(
            TicketId,
            Debug: false,
            OnStep: (id, step) => capturedSteps.Add((id, step.PhaseName)));

        var result = await chain.RunAsync(options, CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);

        // The child phase steps (plan, implement, review, ship) must carry the child's ticket ID.
        var child1PhaseSteps = capturedSteps.Where(s => s.phase != "chain" && s.id == "TLB-2").ToList();
        var child2PhaseSteps = capturedSteps.Where(s => s.phase != "chain" && s.id == "TLB-3").ToList();

        // Both children should have had their phases attributed to the correct child ID.
        Assert.NotEmpty(child1PhaseSteps);
        Assert.NotEmpty(child2PhaseSteps);

        // No child phase step should carry the parent's ticket ID.
        var parentPhaseMislabeled = capturedSteps.Where(s => s.phase != "chain" && s.id == TicketId).ToList();
        Assert.Empty(parentPhaseMislabeled);
    }

    [Fact]
    public async Task RunAsync_ParentChain_DebugCaptureDirectory_IsScopedPerChildPhaseAttempt()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var captureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var baseOptions = MakeBaseOptions() with { DebugCaptureDirectory = captureRoot };

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers, baseOptions: baseOptions);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, Debug: false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);

        var implCaptureDirs = implWorker.SeenOptions
            .Select(o => o.DebugCaptureDirectory)
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();

        Assert.Contains(implCaptureDirs, p => p.EndsWith(Path.Combine("TLB-2", "implement", "round-0"), StringComparison.Ordinal));
        Assert.Contains(implCaptureDirs, p => p.EndsWith(Path.Combine("TLB-3", "implement", "round-0"), StringComparison.Ordinal));
        Assert.Equal(implCaptureDirs.Count, implCaptureDirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var indexPath = Path.Combine(captureRoot, "session-index.txt");
        Assert.True(File.Exists(indexPath));
        var index = File.ReadAllText(indexPath);
        Assert.Contains(Path.Combine("TLB-2", "implement", "round-0"), index);
        Assert.Contains(Path.Combine("TLB-3", "implement", "round-0"), index);
    }

    // -------------------------------------------------------------------------
    // Batch implement tests
    // -------------------------------------------------------------------------

    // AC1+AC2+AC4: parent with 2 Ready children declared in a batch group runs exactly
    // one batch worker session inside the shared chain worktree, and both children
    // receive BatchImplemented outcomes with per-ticket commit SHAs from the Tickets array.
    // AC5 (TLB-449): each child gets a per-ticket implemented_at marker comment and is
    // transitioned to InReview, matching single-ticket observable state.
    [Fact]
    public async Task RunAsync_BatchGroup_TwoReadyChildren_OneWorkerSession_BothBatchImplemented()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // No verifiers enqueued: batch tickets skip review and ship.

        var perTicketResults = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new BatchTicketResult("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: perTicketResults);

        var git = new FakeGitClientChain();
        // Seed log: "bbb111" (stack_pos 1, newest) before "aaa000" (stack_pos 0, oldest).
        git.LogShasResult = new[] { "bbb111", "aaa000" };
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        // AC1: two worker sessions - one batch implement session + one combined review pass.
        Assert.Equal(2, batchWorker.CallCount);
        // Parent completed because all batch children succeeded and review passed.
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        // AC4: both children have BatchImplemented outcome.
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.BatchImplemented, r.Outcome));
        // Per-ticket commit SHAs from the Tickets array appear in FinalRationale.
        var r2 = result.ChildResults.First(r => r.TicketId == "TLB-2");
        Assert.Contains("aaa000", r2.FinalRationale);
        var r3 = result.ChildResults.First(r => r.TicketId == "TLB-3");
        Assert.Contains("bbb111", r3.FinalRationale);

        // AC5 (TLB-449): each child has an implemented_at marker comment with its own SHA.
        var comments2 = ticketing.PostedComments.Where(c => c.id == "TLB-2").ToList();
        Assert.NotEmpty(comments2);
        Assert.Contains("[implemented_at: aaa000]", comments2[0].html);
        Assert.Contains("(branch ", comments2[0].html);
        var comments3 = ticketing.PostedComments.Where(c => c.id == "TLB-3").ToList();
        Assert.NotEmpty(comments3);
        Assert.Contains("[implemented_at: bbb111]", comments3[0].html);
        Assert.Contains("(branch ", comments3[0].html);
        // Each child ended in InReview.
        Assert.Contains(("TLB-2", TicketState.InReview), ticketing.Transitions);
        Assert.Contains(("TLB-3", TicketState.InReview), ticketing.Transitions);
    }

    // AC3: when no BatchImplementGroup is declared the batch worker is never called;
    // children run the normal per-ticket plan/implement/review/ship loop.
    [Fact]
    public async Task RunAsync_NoBatchGroupInOptions_BatchWorkerNotCalled_ChildrenComplete()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Backlog);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Backlog);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var batchWorker = new BatchFakeWorkerAgent();

        var git = new FakeGitClientChain();
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        // No BatchImplementGroup in options.
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false),
            CancellationToken.None);

        Assert.Equal(0, batchWorker.CallCount);
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    // Failure path: when the batch worker returns Failed the first batch ticket stops the
    // parent chain and the parent outcome is ParentStoppedEarly.
    [Fact]
    public async Task RunAsync_BatchGroup_WorkerFails_ParentStoppedEarly()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var batchWorker = new BatchFakeWorkerAgent(status: Status.Failed);

        var git = new FakeGitClientChain();
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        // Batch worker was called once; it failed.
        Assert.Equal(1, batchWorker.CallCount);
        // Parent stopped early because batch implement failed.
        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        // The loop breaks after recording the first StoppedAtImplement result.
        Assert.NotNull(result.ChildResults);
        Assert.NotEmpty(result.ChildResults!);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.StoppedAtImplement, r.Outcome));
    }

    // Commit verification: dirty worktree after batch session -> StoppedAtImplement
    [Fact]
    public async Task RunAsync_BatchGroup_DirtyWorktreeAfterSession_StoppedAtImplement()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var perTicketResults = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new BatchTicketResult("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: perTicketResults);

        var git = new FakeGitClientChain();
        // Worker succeeded but left uncommitted files in the shared worktree.
        git.BatchWorktreeDirtyFiles = new[] { "src/Unfinished.cs" };

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.StoppedAtImplement, r.Outcome));
        Assert.All(result.ChildResults!, r => Assert.Contains("dirty", r.FinalRationale));
    }

    // Commit verification: reported commit absent from branch -> StoppedAtImplement, names ticket
    [Fact]
    public async Task RunAsync_BatchGroup_ReportedCommitAbsentFromBranch_StoppedAtImplement()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var perTicketResults = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new BatchTicketResult("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: perTicketResults);

        var git = new FakeGitClientChain();
        // Log contains only TLB-3's commit; TLB-2's "aaa000" is absent.
        git.LogShasResult = new[] { "bbb111" };

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.StoppedAtImplement, r.Outcome));
        // The failure reason must name the ticket whose commit is missing.
        Assert.All(result.ChildResults!, r => Assert.Contains("TLB-2", r.FinalRationale));
    }

    // Commit verification: commits reported out of declared order -> StoppedAtImplement, names ticket
    [Fact]
    public async Task RunAsync_BatchGroup_CommitsOutOfDeclaredOrder_StoppedAtImplement()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        // Worker says TLB-2 is stack_position 0 (should be oldest) and TLB-3 is
        // stack_position 1 (newest). Log contradicts: "aaa000" is newest (index 0),
        // "bbb111" is oldest (index 1) - the declared order is inverted.
        var perTicketResults = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new BatchTicketResult("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: perTicketResults);

        var git = new FakeGitClientChain();
        // "aaa000" newest (index 0), "bbb111" older (index 1): reversed from stack order.
        git.LogShasResult = new[] { "aaa000", "bbb111" };

        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.StoppedAtImplement, r.Outcome));
        // The failure reason must name the out-of-order ticket (TLB-3 follows TLB-2 but is older).
        Assert.All(result.ChildResults!, r => Assert.Contains("TLB-3", r.FinalRationale));
    }

    // Partial failure: worker commits first ticket then fails on second.
    // AC1: confirmed (committed) ticket gets implemented_at marker and InReview transition.
    // AC2: first incomplete ticket is left InProgress with a recorded failure reason comment.
    // AC3: no child past the failure point is transitioned to InReview.
    [Fact]
    public async Task RunAsync_BatchGroup_PartialFailure_ConfirmedTicketAdvanced_IncompleteStopsChain()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        // Worker commits TLB-2 (stack_position=1) then fails before TLB-3.
        var partialTickets = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 1, Array.Empty<string>(), "SUMMARY_1"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: partialTickets, status: Status.Failed);

        var git = new FakeGitClientChain();
        // Only TLB-2's commit is in the branch; worktree is clean.
        git.LogShasResult = new[] { "aaa000" };
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        Assert.Equal(1, batchWorker.CallCount);
        // Parent stopped early because TLB-3 is incomplete.
        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);

        // TLB-2 was confirmed: BatchImplemented with its commit SHA.
        var tlb2Result = result.ChildResults!.FirstOrDefault(r => r.TicketId == "TLB-2");
        Assert.NotNull(tlb2Result);
        Assert.Equal(ChainOutcome.BatchImplemented, tlb2Result!.Outcome);
        Assert.Contains("aaa000", tlb2Result.FinalRationale);

        // AC1: TLB-2 gets an implemented_at marker comment.
        var comments2 = ticketing.PostedComments.Where(c => c.id == "TLB-2").ToList();
        Assert.NotEmpty(comments2);
        Assert.Contains("[implemented_at: aaa000]", comments2[0].html);

        // AC1: TLB-2 is transitioned to InReview.
        Assert.Contains(("TLB-2", TicketState.InReview), ticketing.Transitions);

        // AC3: TLB-3 is NOT transitioned to InReview (stays InProgress from initial transition).
        Assert.DoesNotContain(("TLB-3", TicketState.InReview), ticketing.Transitions);

        // AC2: TLB-3 gets a failure reason comment.
        var comments3 = ticketing.PostedComments.Where(c => c.id == "TLB-3").ToList();
        Assert.NotEmpty(comments3);
        Assert.Contains("batch implement stopped", comments3[0].html);
        Assert.Contains("batch error", comments3[0].html);
    }

    // Partial failure with verification failure: when the partial commits cannot be
    // confirmed by git, all tickets get StoppedAtImplement (no markers posted).
    [Fact]
    public async Task RunAsync_BatchGroup_PartialFailure_VerificationFails_AllStoppedAtImplement()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        // Worker reports TLB-2 committed but the commit is absent from the branch.
        var partialTickets = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 1, Array.Empty<string>(), "SUMMARY_1"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: partialTickets, status: Status.Failed);

        var git = new FakeGitClientChain();
        // Empty log: BatchCommitVerifier cannot confirm reported SHAs.
        git.LogShasResult = Array.Empty<string>();
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.ExplicitList(new[] { "TLB-2", "TLB-3" });
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.NotEmpty(result.ChildResults!);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.StoppedAtImplement, r.Outcome));

        // No InReview transitions when verification fails.
        Assert.DoesNotContain(("TLB-2", TicketState.InReview), ticketing.Transitions);
        Assert.DoesNotContain(("TLB-3", TicketState.InReview), ticketing.Transitions);

        // No implemented_at markers posted when verification fails.
        var implementedAtComments = ticketing.PostedComments
            .Where(c => c.html.Contains("[implemented_at:"))
            .ToList();
        Assert.Empty(implementedAtComments);
    }

    // -------------------------------------------------------------------------
    // AllEligibleChildren batch tests (TLB-473)
    // -------------------------------------------------------------------------

    // Bare --batch-implement: AllEligibleChildren uses the full eligible set in dispatch order.
    // All children are Ready so both join the batch - same as explicit list with same IDs.
    [Fact]
    public async Task RunAsync_AllEligibleChildren_AllReadyChildren_BatchesAllInOrder()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();

        var perTicketResults = new List<BatchTicketResult>
        {
            new BatchTicketResult("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new BatchTicketResult("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };
        var batchWorker = new BatchFakeWorkerAgent(tickets: perTicketResults);

        var git = new FakeGitClientChain();
        git.LogShasResult = new[] { "bbb111", "aaa000" };
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        // AllEligibleChildren: no explicit list - discovers children at runtime.
        var batchGroup = new ChainBatchImplementGroup.AllEligibleChildren();
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        // Both children batched: one batch-implement session + one combined review pass = 2 calls.
        Assert.Equal(2, batchWorker.CallCount);
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.BatchImplemented, r.Outcome));
    }

    // AllEligibleChildren with no eligible children (all Done): batch worker never called,
    // falls through to no-children behavior (ParentCompleted with empty child list).
    [Fact]
    public async Task RunAsync_AllEligibleChildren_NoEligibleChildren_BatchWorkerNotCalled()
    {
        var parent = MakeTicket(TicketState.Backlog);
        // Both children are Done: not eligible for batching or per-ticket chain.
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Done);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Done);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        var batchWorker = new BatchFakeWorkerAgent();

        var git = new FakeGitClientChain();
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true);

        var batchGroup = new ChainBatchImplementGroup.AllEligibleChildren();
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        // Batch worker never invoked when there are no eligible (non-Done/Cancelled) children.
        Assert.Equal(0, batchWorker.CallCount);
        // Parent chain completes with no child results (all children were already terminal).
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
    }

    // AllEligibleChildren: when auto-assembled group exceeds the max_tickets cap the chain
    // falls back to per-ticket dispatch for all children rather than running a batch session.
    [Fact]
    public async Task RunAsync_AllEligibleChildren_CapExceeded_FallsBackToPerTicket()
    {
        var parent = MakeTicket(TicketState.Backlog);
        var child1 = MakeChildTicket("TLB-2", "child-uuid-1", TicketState.Ready);
        var child2 = MakeChildTicket("TLB-3", "child-uuid-2", TicketState.Ready);

        var ticketing = new ChainFakeTicketing(parent);
        ticketing.SeedChildren("ticket-uuid-1", new[] { child1, child2 });

        var planWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var implWorker = new FakeWorkerAgent(OkWorkerResult().Metadata, blocks: OkWorkerResult().Blocks);
        var verifiers = new Queue<IVerifier>();
        // Two children, two reviews needed for per-ticket path.
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        verifiers.Enqueue(new FakeVerifierChain(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));
        var batchWorker = new BatchFakeWorkerAgent();

        var git = new FakeGitClientChain();
        // Cap of 1 ticket: 2 children will exceed it, triggering per-ticket fallback.
        var tightCaps = new BuildOptions(
            SessionId: "test",
            WorkerName: "claude-code",
            WorkerTimeout: TimeSpan.FromMinutes(5),
            BatchMaxTickets: 1);
        var chain = BuildChain(ticketing, planWorker, implWorker, verifiers,
            batchWorker: batchWorker, git: git, forwardGitToChain: true, baseOptions: tightCaps);

        var batchGroup = new ChainBatchImplementGroup.AllEligibleChildren();
        var result = await chain.RunAsync(
            new ChainPhaseOptions(TicketId, false, BatchImplementGroup: batchGroup),
            CancellationToken.None);

        // Cap exceeded: batch worker not called; per-ticket chain ran for both children.
        Assert.Equal(0, batchWorker.CallCount);
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        // Per-ticket chain runs each child through full plan/implement/review/ship.
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    private sealed class FakeGitClientChain : IGitClient
    {
        private readonly bool _shipFails;
        private readonly List<WorktreeInfo> _worktrees = new();
        private readonly IReadOnlyList<string> _stashEntries;
        private readonly IReadOnlyList<string> _trackedChanges;
        private readonly int _revListCount;
        private readonly string _currentBranch;
        public List<string> RemovedWorktrees { get; } = new();
        public List<string> DeletedBranches { get; } = new();
        public List<string> WorkingDirectoriesSeenForTrackedChanges { get; } = new();
        public int GetTrackedChangesCallCount => WorkingDirectoriesSeenForTrackedChanges.Count;
        public int GetTrackedChangesCallsOnMainWorktree { get; private set; }
        public int GetTrackedChangesCallsBeforeSharedWorktreeCreation { get; private set; }
        public int CreateWorktreeCallCount { get; private set; }

        // When non-null, RebaseAsync dequeues one response per call (falls back to
        // _shipFails-driven default when the queue is empty).
        public Queue<RebaseResult>? RebaseResponses { get; set; }

        // Tracks how many times RebaseAbortAsync was called.
        public int RebaseAbortCallCount { get; private set; }

        // When true, IsAncestorAsync returns false for all calls, triggering the diverged
        // path in ShipPhase (B02 auto-rebase). Defaults to false for backward compatibility.
        public bool TriggerDivergence { get; set; }

        // Returned by ProbeDivergenceAsync when TriggerDivergence is true.
        // Default DivergedWithConflict is safe (no B02 attempted without NoAutoMerge flag).
        public DivergenceState DivergenceStateResult { get; set; } = DivergenceState.DivergedWithConflict;

        // Returned by LogShasAsync. Default null = empty list (interface default).
        // Seed this with the expected commit SHAs (newest first) for batch session tests.
        public IReadOnlyList<string>? LogShasResult { get; set; }

        // When non-null, GetTrackedChangesAsync returns this for paths that would
        // otherwise return empty (i.e., worktree paths and /fake/worktree).
        // Used to simulate a dirty shared worktree after a batch session.
        public IReadOnlyList<string>? BatchWorktreeDirtyFiles { get; set; }

        public FakeGitClientChain(
            bool shipFails = false,
            IReadOnlyList<string>? stashEntries = null,
            int revListCount = 0,
            IReadOnlyList<string>? trackedChanges = null,
            string currentBranch = "main")
        {
            _shipFails = shipFails;
            _stashEntries = stashEntries ?? Array.Empty<string>();
            _trackedChanges = trackedChanges ?? Array.Empty<string>();
            _revListCount = revListCount;
            _currentBranch = currentBranch;
            // Seed the default single-ticket worktree for backward compatibility.
            _worktrees.Add(new WorktreeInfo("/fake/worktree", BranchName, CommitSha, false, false));
        }

        public Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_currentBranch);

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_stashEntries);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        {
            RemovedWorktrees.Add(path);
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            CreateWorktreeCallCount++;
            Directory.CreateDirectory(worktreePath);
            // Track the created worktree so ListWorktreesAsync can find it during ship.
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, CommitSha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct)
        {
            _worktrees.RemoveAll(w => string.Equals(w.Path, worktreePath, StringComparison.OrdinalIgnoreCase));
            _worktrees.Add(new WorktreeInfo(worktreePath, branch, CommitSha, false, false));
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
        {
            WorkingDirectoriesSeenForTrackedChanges.Add(workingDirectory);
            if (!workingDirectory.Contains(".worktrees", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(workingDirectory, "/fake/worktree", StringComparison.OrdinalIgnoreCase))
            {
                GetTrackedChangesCallsOnMainWorktree++;
                if (CreateWorktreeCallCount == 0)
                    GetTrackedChangesCallsBeforeSharedWorktreeCreation++;
                return Task.FromResult(_trackedChanges);
            }

            if (BatchWorktreeDirtyFiles is not null)
                return Task.FromResult(BatchWorktreeDirtyFiles);

            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(_shipFails ? "0000000000000000000000000000000000000000" : CommitSha);

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
        {
            if (RebaseResponses != null && RebaseResponses.Count > 0)
                return Task.FromResult(RebaseResponses.Dequeue());
            if (_shipFails)
                return Task.FromResult(new RebaseResult(false, false, Array.Empty<string>(), "rebase failed for test"));
            return Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));
        }

        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct)
        {
            RebaseAbortCallCount++;
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            DeletedBranches.Add(branch);
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_revListCount);

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> LogShasAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(LogShasResult ?? (IReadOnlyList<string>)Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(!TriggerDivergence);

        public Task<DivergenceState> ProbeDivergenceAsync(string mainWorktreePath, string baseBranch, string remote, CancellationToken ct) =>
            Task.FromResult(DivergenceStateResult);
    }
}
