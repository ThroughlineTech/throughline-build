using System.Text.Json;
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

    private static IReadOnlyDictionary<string, object> OkWorkerMeta() => new Dictionary<string, object>
    {
        ["commit_sha"] = CommitSha,
        ["plan_body_ref"] = "PLAN_BODY",
        ["risk_label"] = "low",
        ["size_label"] = "s",
        ["planned_at_sha"] = MainSha,
        ["files_changed"] = Array.Empty<string>()
    };

    private static IReadOnlyDictionary<string, string> OkWorkerBlocks() => new Dictionary<string, string>
    {
        ["PLAN_BODY"] = "# Plan\nThis is the plan."
    };

    private int _sessionCounter;
    private string NextSessionId() => $"session-{++_sessionCounter}";

    private (ChainPhase chain, CapturingEventSink sink) BuildChain(
        EventChainFakeTicketing ticketing,
        IWorkerAgent planWorker,
        EventFakeWorkerAgent implWorker,
        Queue<IVerifier> verifierQueue,
        EventFakeGitClient? git = null,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory = null,
        CapturingEventSink? eventSink = null,
        Func<BuildOptions, GatePhase>? gateFactory = null)
    {
        _sessionCounter = 0;
        var sink = eventSink ?? new CapturingEventSink();
        git ??= new EventFakeGitClient();

        var baseOpts = MakeBaseOptions();

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, sink, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, sink, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory = (opts, _) =>
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
            workingDirectory: WorkDir,
            ratifierFactory: ratifierFactory,
            gateFactory: gateFactory);

        return (chain, sink);
    }

    private static CheckResult GatingPass(string name = "build", int elapsedMs = 500) =>
        new CheckResult(name, true, 0, "", "", TimeSpan.FromMilliseconds(elapsedMs), CheckRole.Gating);

    private static CheckResult GatingFail(string name = "build", int elapsedMs = 300) =>
        new CheckResult(name, false, 1, "", "error", TimeSpan.FromMilliseconds(elapsedMs), CheckRole.Gating);

    // Test 1: Happy path emits ChainStart, per-phase events, ChainEnd in order; no ReworkRound
    [Fact]
    public async Task HappyPath_EmitsChainStartThenPerPhaseEventsThenChainEnd_NoReworkRound()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
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
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
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
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
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
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
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

    // Test 5: RefusedInitialState emits ChainStart then ChainEnd with phases_run=0, no other events between.
    // Uses a terminal state (Done) - the only states the chain still refuses now that Planning/InProgress resume.
    [Fact]
    public async Task RefusedInitialState_EmitsChainStartAndChainEnd_NothingBetween_PhasesRunZero()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Done));
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

    // Test 6: RatifiedObsolete via plan path emits TicketSubsumed before ChainEnd with evidence fields
    [Fact]
    public async Task RatifiedObsolete_EmitsTicketSubsumedEvent_BeforeChainEnd_WithEvidenceFields()
    {
        const string subsumedCommit = "abc123def456";
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var escalateResult = ObsoleteEscalateWorkerResult(subsumedCommit);
        var planWorker = new EscalateWorkerAgent(escalateResult);
        var ratifier = new FakeObsoleteRatifier(new Verdict(VerdictKind.Pass, "ratified", Array.Empty<string>()));

        var (chain, sink) = BuildChain(
            ticketing,
            planWorker,
            new EventFakeWorkerAgent(null),
            new Queue<IVerifier>(),
            ratifierFactory: _ => ratifier);

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.RatifiedObsolete, result.Outcome);
        Assert.True(ratifier.WasCalled);

        var events = sink.Events;
        var subsumedEvs = events.Where(e => e.Kind == EventKind.TicketSubsumed).ToList();
        Assert.Single(subsumedEvs);

        var subsumedEv = subsumedEvs[0];
        Assert.Equal(Phase.Chain, subsumedEv.Phase);
        Assert.Equal(TicketId, (string)subsumedEv.Data["ticket_id"]);
        Assert.Equal(subsumedCommit, (string)subsumedEv.Data["subsumed_by_commit"]);
        Assert.Equal("already done in prior commit", (string)subsumedEv.Data["rationale"]);

        // TicketSubsumed must appear before ChainEnd
        var subsumedIdx = events.FindIndex(e => e.Kind == EventKind.TicketSubsumed);
        var chainEndIdx = events.FindIndex(e => e.Kind == EventKind.ChainEnd);
        Assert.True(subsumedIdx >= 0 && chainEndIdx >= 0);
        Assert.True(subsumedIdx < chainEndIdx);
    }

    // AC: "Each gated ticket emits one ledger event"
    // When no gate factory is configured, no CostLedger event is emitted.
    [Fact]
    public async Task NoGate_NoCostLedgerEvent()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var (chain, sink) = BuildChain(ticketing, planWorker, implWorker, verifiers);
        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(sink.Events, e => e.Kind == EventKind.CostLedger);
    }

    // AC: "Each gated ticket emits one ledger event" + "Gate wall is populated from measured check durations"
    // When the gate passes on the first attempt, one CostLedger is emitted with the correct
    // gate wall time, zero rework rounds, and annotatable zero slots.
    [Fact]
    public async Task GatePasses_OneCostLedger_WithGateWallMs_ZeroReworkRounds()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var git = new EventFakeGitClient();
        var sink = new CapturingEventSink();
        var (chain, _) = BuildChain(ticketing, planWorker, implWorker, verifiers, git, eventSink: sink,
            gateFactory: opts => new GatePhase(ticketing, sink, opts,
                new GateOptions(Array.Empty<CheckSpec>()), git,
                new PreComputedChecksRunner(new[] { GatingPass("build", elapsedMs: 500) })));

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var ledgerEvents = sink.Events.Where(e => e.Kind == EventKind.CostLedger).ToList();
        Assert.Single(ledgerEvents);

        var ledger = ledgerEvents[0];
        Assert.Equal(Phase.Gate, ledger.Phase);
        Assert.Equal(TicketId, ledger.TicketId);
        Assert.Equal(500L, (long)ledger.Data["gate_wall_ms"]);
        Assert.Equal(0, (int)ledger.Data["gate_attributable_rework_rounds"]);
        Assert.Equal(0, (int)ledger.Data["cascade_caught"]);
        Assert.Equal(0, (int)ledger.Data["false_fails"]);
        // No rework rounds, so no token fields
        Assert.False(ledger.Data.ContainsKey("gate_attributable_rework_input_tokens"));
        Assert.False(ledger.Data.ContainsKey("gate_attributable_rework_output_tokens"));
        Assert.False(ledger.Data.ContainsKey("gate_attributable_rework_tokens_available"));

        // CostLedger emitted before ChainEnd
        var ledgerIdx = sink.Events.FindIndex(e => e.Kind == EventKind.CostLedger);
        var chainEndIdx = sink.Events.FindIndex(e => e.Kind == EventKind.ChainEnd);
        Assert.True(ledgerIdx < chainEndIdx);
    }

    // AC: "Gate-attributable rework tokens are populated from rework rounds whose trigger was a gate hard-fail"
    //     "marked unavailable where token accounting is absent"
    // Gate fails once (triggering one rework round), then passes. The implement fake has no llm_usage,
    // so tokens are marked unavailable. CostLedger reflects 1 gate-attributable rework round.
    [Fact]
    public async Task GateFailsThenPasses_OneCostLedger_OneReworkRound_TokensUnavailable()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var git = new EventFakeGitClient();
        var sink = new CapturingEventSink();
        var gateCheckQueue = new Queue<CheckResult[]>();
        gateCheckQueue.Enqueue(new[] { GatingFail("build", elapsedMs: 300) }); // round 0: gate fails
        gateCheckQueue.Enqueue(new[] { GatingPass("build", elapsedMs: 400) }); // round 1: gate passes

        var (chain, _) = BuildChain(ticketing, planWorker, implWorker, verifiers, git, eventSink: sink,
            gateFactory: opts => new GatePhase(ticketing, sink, opts,
                new GateOptions(Array.Empty<CheckSpec>()), git,
                new PreComputedChecksRunner(gateCheckQueue.Dequeue())));

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var ledgerEvents = sink.Events.Where(e => e.Kind == EventKind.CostLedger).ToList();
        Assert.Single(ledgerEvents);

        var ledger = ledgerEvents[0];
        Assert.Equal(700L, (long)ledger.Data["gate_wall_ms"]); // 300 + 400
        Assert.Equal(1, (int)ledger.Data["gate_attributable_rework_rounds"]);
        // No llm_usage in fake worker: tokens marked unavailable
        Assert.False((bool)ledger.Data["gate_attributable_rework_tokens_available"]);
        Assert.False(ledger.Data.ContainsKey("gate_attributable_rework_input_tokens"));
        Assert.Equal(0, (int)ledger.Data["cascade_caught"]);
        Assert.Equal(0, (int)ledger.Data["false_fails"]);
    }

    // AC: "Gate-attributable rework tokens are populated from rework rounds whose trigger was a gate hard-fail"
    // Gate fails once; the rework implement returns llm_usage with non-zero tokens.
    // CostLedger includes the token sums for gate-attributable rework.
    [Fact]
    public async Task GateFailsThenPasses_TokensPresent_CostLedgerIncludesTokenCounts()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        // Implement worker includes llm_usage so token accounting is available
        var implMeta = new Dictionary<string, object>(OkWorkerMeta())
        {
            ["llm_usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = (object)50000,
                ["output_tokens"] = (object)2000,
                ["model"] = "claude-code",
                ["vendor"] = "anthropic",
                ["wall_clock_ms"] = 30000L
            }
        };
        var implWorker = new EventFakeWorkerAgent(implMeta, blocks: OkWorkerBlocks());
        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new EventFakeVerifier(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>())));

        var git = new EventFakeGitClient();
        var sink = new CapturingEventSink();
        var gateCheckQueue = new Queue<CheckResult[]>();
        gateCheckQueue.Enqueue(new[] { GatingFail("build", elapsedMs: 200) }); // round 0 fails
        gateCheckQueue.Enqueue(new[] { GatingPass("build", elapsedMs: 100) }); // round 1 passes

        var (chain, _) = BuildChain(ticketing, planWorker, implWorker, verifiers, git, eventSink: sink,
            gateFactory: opts => new GatePhase(ticketing, sink, opts,
                new GateOptions(Array.Empty<CheckSpec>()), git,
                new PreComputedChecksRunner(gateCheckQueue.Dequeue())));

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.Completed, result.Outcome);

        var ledgerEvents = sink.Events.Where(e => e.Kind == EventKind.CostLedger).ToList();
        Assert.Single(ledgerEvents);

        var ledger = ledgerEvents[0];
        Assert.Equal(1, (int)ledger.Data["gate_attributable_rework_rounds"]);
        // Tokens from the gate-attributable rework round (round 1 implement only, not round 0)
        Assert.Equal(50000L, (long)ledger.Data["gate_attributable_rework_input_tokens"]);
        Assert.Equal(2000L, (long)ledger.Data["gate_attributable_rework_output_tokens"]);
        Assert.False(ledger.Data.ContainsKey("gate_attributable_rework_tokens_available"));
    }

    // AC: "Each gated ticket emits one ledger event" with ReworkCapExceeded outcome
    // Gate always fails; rework cap is hit. CostLedger emitted once for the ticket.
    [Fact]
    public async Task GateAlwaysFails_ReworkCapExceeded_OneCostLedgerWithReworkRounds()
    {
        var ticketing = new EventChainFakeTicketing(MakeTicket(TicketState.Backlog));
        var planWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var implWorker = new EventFakeWorkerAgent(OkWorkerMeta(), blocks: OkWorkerBlocks());
        var verifiers = new Queue<IVerifier>();

        var git = new EventFakeGitClient();
        var sink = new CapturingEventSink();
        // Gate always fails - will hit MaxReworkRounds=2 (rounds 0, 1, 2)
        var (chain, _) = BuildChain(ticketing, planWorker, implWorker, verifiers, git, eventSink: sink,
            gateFactory: opts => new GatePhase(ticketing, sink, opts,
                new GateOptions(Array.Empty<CheckSpec>()), git,
                new PreComputedChecksRunner(new[] { GatingFail("build", elapsedMs: 100) })));

        var result = await chain.RunAsync(new ChainPhaseOptions(TicketId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ReworkCapExceeded, result.Outcome);

        var ledgerEvents = sink.Events.Where(e => e.Kind == EventKind.CostLedger).ToList();
        Assert.Single(ledgerEvents);

        var ledger = ledgerEvents[0];
        Assert.Equal(Phase.Gate, ledger.Phase);
        // 3 gate runs (rounds 0, 1, 2), each 100ms = 300ms total
        Assert.Equal(300L, (long)ledger.Data["gate_wall_ms"]);
        // 2 gate-attributable rework rounds (rounds 1 and 2 were triggered by gate failures)
        Assert.Equal(2, (int)ledger.Data["gate_attributable_rework_rounds"]);
    }

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
        public Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, string? evidenceDirectory, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(_verdict);
        }
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
    
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

    public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class EventFakeWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;
        private readonly bool _fail;

        public EventFakeWorkerAgent(IReadOnlyDictionary<string, object>? metadata, bool fail = false,
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

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            Directory.CreateDirectory(worktreePath);
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

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
