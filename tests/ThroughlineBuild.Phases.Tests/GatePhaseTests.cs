using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class GatePhaseTests
{
    private const string TicketId = "TLB-1";
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";

    private static BuildOptions MakeBuildOptions() => new BuildOptions(
        SessionId: "gate-session",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static CheckResult Pass(string name, CheckRole role = CheckRole.Gating) =>
        new CheckResult(name, Passed: true, ExitCode: 0, StdoutTail: "", StderrTail: "", Elapsed: TimeSpan.Zero, Role: role);

    private static CheckResult Fail(string name, CheckRole role = CheckRole.Gating) =>
        new CheckResult(name, Passed: false, ExitCode: 1, StdoutTail: "", StderrTail: "error output", Elapsed: TimeSpan.Zero, Role: role);

    private GatePhase MakeGate(
        FakeGateTicketing? ticketing = null,
        FakeGateEventSink? events = null,
        IReadOnlyList<CheckResult>? checkResults = null,
        FakeGateGitClient? git = null,
        IReadOnlyList<CheckSpec>? specs = null,
        GateVacuityProver? prover = null,
        GateControlProber? controlProber = null,
        Func<IReadOnlyList<CheckSpec>?>? gateChecksReloader = null) =>
        new GatePhase(
            ticketing ?? new FakeGateTicketing(),
            events ?? new FakeGateEventSink(),
            MakeBuildOptions(),
            new GateOptions(specs ?? Array.Empty<CheckSpec>()),
            git ?? new FakeGateGitClient(),
            new PreComputedChecksRunner(checkResults ?? Array.Empty<CheckResult>()),
            prover,
            controlProber,
            gateChecksReloader);

    // A gating CheckSpec carrying a dummy canary so the prover loop reaches the prover.
    // The fake prover ignores the canary; the canary only matters for the real prover.
    private static CheckSpec GatingSpec(string name = "build") =>
        new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromMinutes(1),
            CheckRole.Gating, new[] { new CanaryFile("p", "c") });

    private static CheckSpec AdvisorySpec(string name = "lint") =>
        new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromMinutes(1),
            CheckRole.Advisory, new[] { new CanaryFile("p", "c") });

    private static CheckSpec SetupSpec(string name = "xcodegen") =>
        new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromMinutes(1), CheckRole.Setup);

    // Deterministic fake prover: returns a fixed verdict without touching disk or git.
    private sealed class FakeVacuityProver : GateVacuityProver
    {
        private readonly GateVacuityVerdict _verdict;
        public int Calls { get; private set; }
        public FakeVacuityProver(GateVacuityOutcome outcome, string check = "build", string? reason = "r")
            => _verdict = new GateVacuityVerdict(outcome, check, reason);
        public override Task<GateVacuityVerdict> ProveAsync(CheckSpec spec, AutomatedChecksRunner runner, IGitClient git, string worktreePath, CancellationToken ct)
        { Calls++; return Task.FromResult(_verdict); }
    }

    // --- non-vacuity prover wiring (commit 4) ---

    [Fact]
    public async Task RunAsync_GreenGatingCheck_ProverVacuous_HardFailsWithoutRework()
    {
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var prover = new FakeVacuityProver(GateVacuityOutcome.Vacuous, check: "build", reason: "build is vacuous");
        var gate = MakeGate(ticketing, events,
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Vacuous);
        Assert.False(outcome.Passed);
        Assert.NotNull(outcome.HardFailReason);
        Assert.Contains("build", outcome.HardFailReason);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_vacuous");
        // No rework transition: a config defect must not bounce the ticket back to InProgress.
        Assert.DoesNotContain((TicketId, TicketState.InProgress), ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_GreenGatingCheck_ProverOk_GatePasses()
    {
        var prover = new FakeVacuityProver(GateVacuityOutcome.Ok);
        var gate = MakeGate(
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.False(outcome.Vacuous);
    }

    [Fact]
    public async Task RunAsync_GreenGatingCheck_ProverUnverified_GatePasses_EmitsAdvisory()
    {
        var events = new FakeGateEventSink();
        var prover = new FakeVacuityProver(GateVacuityOutcome.Unverified);
        var gate = MakeGate(events: events,
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.False(outcome.Vacuous);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_unverified");
    }

    [Fact]
    public async Task RunAsync_GreenGatingCheck_ProverCleanupFailed_HardFails()
    {
        var events = new FakeGateEventSink();
        var prover = new FakeVacuityProver(GateVacuityOutcome.CleanupFailed);
        var gate = MakeGate(events: events,
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Vacuous);
        Assert.False(outcome.Passed);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_canary_cleanup_failed");
    }

    [Fact]
    public async Task RunAsync_RedGatingCheck_ProverNeverRuns()
    {
        var ticketing = new FakeGateTicketing();
        var prover = new FakeVacuityProver(GateVacuityOutcome.Ok);
        var gate = MakeGate(ticketing,
            checkResults: new[] { Fail("build") },
            specs: new[] { GatingSpec("build") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.Equal(0, prover.Calls);
        Assert.False(outcome.Passed);
        Assert.False(outcome.Vacuous);
        // The existing gating_checks_failed path still hard-fails and transitions.
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_NullProver_GreenGatingCheck_NormalPass()
    {
        var gate = MakeGate(
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            prover: null);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.False(outcome.Vacuous);
    }

    [Fact]
    public async Task RunAsync_GreenAdvisoryCheck_ProverNeverRuns()
    {
        var prover = new FakeVacuityProver(GateVacuityOutcome.Vacuous);
        var gate = MakeGate(
            checkResults: new[] { Pass("lint", CheckRole.Advisory) },
            specs: new[] { AdvisorySpec("lint") },
            prover: prover);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.Equal(0, prover.Calls);
        Assert.True(outcome.Passed);
        Assert.False(outcome.Vacuous);
    }

    // AC: "The gate hard-fails only when build, test, or typecheck fails;
    //      lint, format, and smoke signals never hard-fail it"
    [Fact]
    public async Task RunAsync_AdvisoryOnlyFailure_GatePasses_NoTransition()
    {
        var ticketing = new FakeGateTicketing();
        var gate = MakeGate(ticketing, checkResults: new[]
        {
            Pass("build", CheckRole.Gating),
            Fail("lint", CheckRole.Advisory)
        });

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.HardFailReason);
        Assert.Empty(ticketing.Transitions);
        // Advisory failure recorded but does not block
        Assert.Contains(outcome.CheckResults, r => r.Name == "lint" && !r.Passed);
    }

    // AC: "A gate hard-fail transitions the ticket InReview -> InProgress and enters the rework loop"
    [Fact]
    public async Task RunAsync_GatingCheckFails_HardFails_TransitionsToInProgress()
    {
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var gate = MakeGate(ticketing, events, checkResults: new[]
        {
            Fail("build", CheckRole.Gating)
        });

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.NotNull(outcome.HardFailReason);
        Assert.Contains("build", outcome.HardFailReason);
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure);
    }

    // AC: null claim (pre-claim-format worker) bypasses claim validation and proceeds to checks
    [Fact]
    public async Task RunAsync_NullClaim_ProceedsWithoutClaimValidation()
    {
        var gate = MakeGate(checkResults: new[] { Pass("build") });

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Single(outcome.CheckResults);
        Assert.True(outcome.CheckResults[0].Passed);
    }

    // AC: "The gate emits a structured outcome carrying per-check results and smoke signals"
    [Fact]
    public async Task RunAsync_PassingGate_SmokeSignalsIncludeDiffFacts()
    {
        var gate = MakeGate(checkResults: Array.Empty<CheckResult>());

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.NotEmpty(outcome.SmokeSignals);
        Assert.Contains(outcome.SmokeSignals, s => s.Kind == SmokeSignalKind.DiffFacts);
    }

    // AC: "When claims declare consumes and provides, the preflight reports whether consumes
    //      is a subset of accumulated upstream provides"
    [Fact]
    public async Task RunAsync_ConsumesAllSatisfied_EmitsMatchedPreflightSignal()
    {
        var claim = new CompletionClaim(
            Provides: new[] { "module-a" },
            Consumes: new[] { "module-b", "module-c" },
            AcBindings: Array.Empty<AcBinding>(),
            TestsAdded: Array.Empty<string>());
        var upstream = new HashSet<string> { "module-b", "module-c", "module-d" };
        var gate = MakeGate(checkResults: Array.Empty<CheckResult>());

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim, CancellationToken.None, accumulatedUpstreamProvides: upstream);

        Assert.True(outcome.Passed);
        var signal = Assert.Single(outcome.SmokeSignals, s => s.Label == "consumes-provides-preflight");
        Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
        Assert.True(signal.Matched);
        Assert.Contains("2", signal.Details); // "all 2 consume(s) satisfied"
    }

    // AC: "The result is emitted as a smoke signal and never hard-fails the gate"
    [Fact]
    public async Task RunAsync_ConsumesMissingFromUpstream_EmitsUnmatchedSignal_GateStillPasses()
    {
        var claim = new CompletionClaim(
            Provides: new[] { "module-a" },
            Consumes: new[] { "module-b", "module-z" },
            AcBindings: Array.Empty<AcBinding>(),
            TestsAdded: Array.Empty<string>());
        var upstream = new HashSet<string> { "module-b" }; // module-z missing
        var gate = MakeGate(checkResults: Array.Empty<CheckResult>());

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim, CancellationToken.None, accumulatedUpstreamProvides: upstream);

        Assert.True(outcome.Passed);
        var signal = Assert.Single(outcome.SmokeSignals, s => s.Label == "consumes-provides-preflight");
        Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
        Assert.False(signal.Matched);
        Assert.Contains("module-z", signal.Details);
    }

    // AC: "The preflight is a no-op, not a failure, when the fields are absent"
    [Fact]
    public async Task RunAsync_EmptyConsumes_NoPreflightSignalEmitted()
    {
        var claim = new CompletionClaim(
            Provides: new[] { "module-a" },
            Consumes: Array.Empty<string>(),
            AcBindings: Array.Empty<AcBinding>(),
            TestsAdded: Array.Empty<string>());
        var gate = MakeGate(checkResults: Array.Empty<CheckResult>());

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.DoesNotContain(outcome.SmokeSignals, s => s.Label == "consumes-provides-preflight");
    }

    // AC: "The preflight is a no-op, not a failure, when the fields are absent" (null claim)
    [Fact]
    public async Task RunAsync_NullClaim_NoPreflightSignalEmitted()
    {
        var gate = MakeGate(checkResults: Array.Empty<CheckResult>());

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.DoesNotContain(outcome.SmokeSignals, s => s.Label == "consumes-provides-preflight");
    }

    // TLB-523: a failed setup step (a prerequisite codegen/install run) hard-fails the gate and bounces
    // the ticket back to InProgress for rework, reported distinctly as setup_failed.
    [Fact]
    public async Task RunAsync_SetupStepFails_HardFails_TransitionsToInProgress()
    {
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var gate = MakeGate(ticketing, events, checkResults: new[]
        {
            Fail("xcodegen", CheckRole.Setup),
            Fail("build", CheckRole.Gating) // cascade: the build also fails without the prerequisite
        });

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.False(outcome.Vacuous); // setup failure is reworkable, not a vacuity integrity hard-fail
        Assert.NotNull(outcome.HardFailReason);
        Assert.Contains("setup", outcome.HardFailReason);
        Assert.Contains("xcodegen", outcome.HardFailReason);
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "setup_failed");
    }

    [Fact]
    public async Task RunAsync_SetupStepPasses_GateProceedsToPass()
    {
        var gate = MakeGate(checkResults: new[]
        {
            Pass("xcodegen", CheckRole.Setup),
            Pass("build", CheckRole.Gating)
        });

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.False(outcome.Vacuous);
    }

    // -------------------------------------------------------------------------
    // TLB-538: environment-failure classification (base-ref control run)
    // -------------------------------------------------------------------------

    // Deterministic fake control prober: returns a fixed verdict, records what it was asked to run.
    private sealed class FakeControlProber : GateControlProber
    {
        private readonly GateControlVerdict _verdict;
        public int Calls { get; private set; }
        public IReadOnlyList<CheckSpec>? ReceivedChecks { get; private set; }
        public FakeControlProber(GateControlOutcome outcome, IReadOnlyList<CheckResult>? controlResults = null)
            => _verdict = new GateControlVerdict(outcome, "b1b2b3b4b5b6b7b8",
                controlResults ?? Array.Empty<CheckResult>());
        public override Task<GateControlVerdict> ProbeAsync(IReadOnlyList<CheckSpec> checks, string baseRef,
            string mainWorktreePath, AutomatedChecksRunner runner, IGitClient git, CancellationToken ct)
        {
            Calls++;
            ReceivedChecks = checks;
            return Task.FromResult(_verdict);
        }
    }

    // Returns a different result set per RunAsync call, for the config-reload re-run path.
    private sealed class SequencedChecksRunner : AutomatedChecksRunner
    {
        private readonly Queue<IReadOnlyList<CheckResult>> _sequence;
        public int Calls { get; private set; }
        public SequencedChecksRunner(params IReadOnlyList<CheckResult>[] sequence)
            => _sequence = new Queue<IReadOnlyList<CheckResult>>(sequence);
        public override Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs,
            string workingDirectory, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_sequence.Count > 0
                ? _sequence.Dequeue()
                : (IReadOnlyList<CheckResult>)Array.Empty<CheckResult>());
        }
    }

    [Fact]
    public async Task RunAsync_GatingFails_ControlBaseFails_EnvironmentFailure_NoReworkTransition()
    {
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var prober = new FakeControlProber(GateControlOutcome.BaseFails,
            controlResults: new[] { Fail("build") });
        var gate = MakeGate(ticketing, events,
            checkResults: new[] { Fail("build") },
            specs: new[] { GatingSpec("build") },
            controlProber: prober);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.True(outcome.EnvironmentFailure);
        Assert.False(outcome.Vacuous);
        Assert.Equal(1, prober.Calls);
        Assert.NotNull(outcome.HardFailReason);
        Assert.Contains("environment failure", outcome.HardFailReason);
        // Evidence carries the control run's output tail.
        Assert.NotNull(outcome.ControlEvidence);
        Assert.Contains("error output", outcome.ControlEvidence);
        // No rework transition: the ticket stays InReview so a re-run after the env fix resumes cleanly.
        Assert.DoesNotContain((TicketId, TicketState.InProgress), ticketing.Transitions);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_control_run");
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_environment_failure");
    }

    [Fact]
    public async Task RunAsync_GatingFails_ControlBasePasses_NormalReworkPath()
    {
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var prober = new FakeControlProber(GateControlOutcome.BasePasses,
            controlResults: new[] { Pass("build") });
        var gate = MakeGate(ticketing, events,
            checkResults: new[] { Fail("build") },
            specs: new[] { GatingSpec("build") },
            controlProber: prober);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.False(outcome.EnvironmentFailure);
        // Base is green, so the failure is the ticket's: the ordinary rework bounce applies.
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
        Assert.DoesNotContain(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_environment_failure");
    }

    [Fact]
    public async Task RunAsync_GatingFails_ControlInconclusive_NormalReworkPath()
    {
        // A broken prober must never misclassify a real code failure as environmental.
        var ticketing = new FakeGateTicketing();
        var prober = new FakeControlProber(GateControlOutcome.Inconclusive);
        var gate = MakeGate(ticketing,
            checkResults: new[] { Fail("build") },
            specs: new[] { GatingSpec("build") },
            controlProber: prober);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.False(outcome.EnvironmentFailure);
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ControlRun_ReceivesOnlyFailedGatingChecksPlusSetup()
    {
        var prober = new FakeControlProber(GateControlOutcome.BasePasses);
        var gate = MakeGate(
            checkResults: new[]
            {
                Pass("xcodegen", CheckRole.Setup),
                Fail("build"),
                Pass("test"),
                Fail("lint", CheckRole.Advisory)
            },
            specs: new[] { SetupSpec("xcodegen"), GatingSpec("build"), GatingSpec("test"), AdvisorySpec("lint") },
            controlProber: prober);

        await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.NotNull(prober.ReceivedChecks);
        // Setup prerequisites + the failed gating check only - not the green gating check,
        // not the advisory check.
        Assert.Equal(new[] { "xcodegen", "build" }, prober.ReceivedChecks!.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task RunAsync_GatePasses_ControlProberNeverRuns()
    {
        var prober = new FakeControlProber(GateControlOutcome.BaseFails);
        var gate = MakeGate(
            checkResults: new[] { Pass("build") },
            specs: new[] { GatingSpec("build") },
            controlProber: prober);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Equal(0, prober.Calls);
    }

    [Fact]
    public async Task RunAsync_EnvFailure_ConfigChangedOnDisk_RerunGreen_Recovers()
    {
        // The incident shape (TLB-538): the gate fails on a stale config, a worker/operator fixed
        // .build/config.toml mid-run, and the orchestrator - which loaded config once at startup -
        // must pick up the fix and continue instead of stopping.
        var ticketing = new FakeGateTicketing();
        var events = new FakeGateEventSink();
        var staleSpecs = new[] { GatingSpec("build") };
        var freshSpecs = new[] { new CheckSpec("build", "noop", new[] { "fixed-destination" },
            TimeSpan.FromMinutes(1), CheckRole.Gating) };
        var runner = new SequencedChecksRunner(
            new[] { Fail("build") },   // first run: stale specs fail
            new[] { Pass("build") });  // re-run with fresh specs: green
        var prober = new FakeControlProber(GateControlOutcome.BaseFails,
            controlResults: new[] { Fail("build") });
        var gate = new GatePhase(ticketing, events, MakeBuildOptions(),
            new GateOptions(staleSpecs), new FakeGateGitClient(), runner,
            vacuityProver: null, controlProber: prober, gateChecksReloader: () => freshSpecs);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.False(outcome.EnvironmentFailure);
        Assert.Equal(2, runner.Calls);
        Assert.Empty(ticketing.Transitions);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_config_reloaded"
            && e.Data.TryGetValue("recovered", out var rec) && (bool)rec);
    }

    [Fact]
    public async Task RunAsync_EnvFailure_ConfigUnchangedOnDisk_NoRerun_EnvironmentFailure()
    {
        var staleSpecs = new[] { GatingSpec("build") };
        var runner = new SequencedChecksRunner(new[] { Fail("build") });
        var prober = new FakeControlProber(GateControlOutcome.BaseFails,
            controlResults: new[] { Fail("build") });
        var gate = new GatePhase(new FakeGateTicketing(), new FakeGateEventSink(), MakeBuildOptions(),
            new GateOptions(staleSpecs), new FakeGateGitClient(), runner,
            vacuityProver: null, controlProber: prober, gateChecksReloader: () => staleSpecs);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.EnvironmentFailure);
        // Equivalent on-disk specs must not trigger a wasted re-run.
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task RunAsync_EnvFailure_ConfigChangedButStillRed_EnvironmentFailure()
    {
        var events = new FakeGateEventSink();
        var staleSpecs = new[] { GatingSpec("build") };
        var freshSpecs = new[] { new CheckSpec("build", "noop", new[] { "still-broken" },
            TimeSpan.FromMinutes(1), CheckRole.Gating) };
        var runner = new SequencedChecksRunner(
            new[] { Fail("build") },
            new[] { Fail("build") });
        var prober = new FakeControlProber(GateControlOutcome.BaseFails,
            controlResults: new[] { Fail("build") });
        var gate = new GatePhase(new FakeGateTicketing(), events, MakeBuildOptions(),
            new GateOptions(staleSpecs), new FakeGateGitClient(), runner,
            vacuityProver: null, controlProber: prober, gateChecksReloader: () => freshSpecs);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.True(outcome.EnvironmentFailure);
        Assert.Equal(2, runner.Calls);
        Assert.Contains(events.Events, e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (string)k == "gate_config_reloaded"
            && e.Data.TryGetValue("recovered", out var rec) && !(bool)rec);
    }

    [Fact]
    public async Task RunAsync_NoControlProber_GatingFails_NormalReworkPath_Unchanged()
    {
        // Null prober disables classification entirely (the pre-TLB-538 behavior).
        var ticketing = new FakeGateTicketing();
        var gate = MakeGate(ticketing,
            checkResults: new[] { Fail("build") },
            specs: new[] { GatingSpec("build") },
            controlProber: null);

        var outcome = await gate.RunAsync(
            TicketId, "/fake/worktree", "ticket/tlb-1", MainSha, "/fake/working",
            claim: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.False(outcome.EnvironmentFailure);
        Assert.Contains((TicketId, TicketState.InProgress), ticketing.Transitions);
    }

    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeGateTicketing : ITicketing
    {
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
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
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Task.CompletedTask;
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeGateEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // Minimal IGitClient for GatePhase: only DiffAsync needs a real implementation.
    private sealed class FakeGateGitClient : IGitClient
    {
        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("0000000000000000000000000000000000000000");
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult("0000000000000000000000000000000000000000");
        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
