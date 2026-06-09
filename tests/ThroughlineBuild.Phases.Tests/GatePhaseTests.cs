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
        GateVacuityProver? prover = null) =>
        new GatePhase(
            ticketing ?? new FakeGateTicketing(),
            events ?? new FakeGateEventSink(),
            MakeBuildOptions(),
            new GateOptions(specs ?? Array.Empty<CheckSpec>()),
            git ?? new FakeGateGitClient(),
            new PreComputedChecksRunner(checkResults ?? Array.Empty<CheckResult>()),
            prover);

    // A gating CheckSpec carrying a dummy canary so the prover loop reaches the prover.
    // The fake prover ignores the canary; the canary only matters for the real prover.
    private static CheckSpec GatingSpec(string name = "build") =>
        new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromMinutes(1),
            CheckRole.Gating, new[] { new CanaryFile("p", "c") });

    private static CheckSpec AdvisorySpec(string name = "lint") =>
        new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromMinutes(1),
            CheckRole.Advisory, new[] { new CanaryFile("p", "c") });

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
