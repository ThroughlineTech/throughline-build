using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ShipPhaseTests
{
    private const string TicketId = "TLB-1";
    private const string TicketTitle = "Test ticket";
    private const string BranchName = "ticket/tlb-1";
    private const string MergedSha = "0123456789abcdef0123456789abcdef01234567";

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

    private static BuildOptions MakeBuildOptions() => new BuildOptions(
        SessionId: "session-1",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static ShipOptions MakeShipOptions(
        IReadOnlyList<CheckSpec>? checks = null,
        bool deleteFeatureBranch = true,
        bool noAutoMerge = false,
        string? targetBranch = null,
        bool noPush = false,
        bool targetBranchOverridden = false,
        bool skipBaseline = false,
        BaselineCache? baselineCache = null) => new ShipOptions(
            RegressionChecks: checks ?? Array.Empty<CheckSpec>(),
            Remote: "origin",
            BaseBranch: "main",
            DeleteFeatureBranch: deleteFeatureBranch,
            NoAutoMerge: noAutoMerge,
            TargetBranch: targetBranch,
            NoPush: noPush,
            TargetBranchOverridden: targetBranchOverridden,
            SkipBaseline: skipBaseline,
            BaselineCache: baselineCache);

    private static string MakeWorkingDir() => Directory.GetCurrentDirectory();

    private static ConflictMarkerScannerFn EmptyScanner() =>
        (paths, ct) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>());

    private static ConflictMarkerScannerFn HitsScanner(IReadOnlyList<ConflictMarkerHit> hits) =>
        (paths, ct) => Task.FromResult(hits);

    [Fact]
    public async Task RunAsync_HappyPath_SuccessShipsTicket()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.Null(result.FailedAt);
        Assert.Equal(MergedSha, result.MergedSha);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);

        Assert.Single(ticketing.Comments);
        Assert.Contains("[shipped_at: ", ticketing.Comments[0].html);
        Assert.Contains(MergedSha, ticketing.Comments[0].html);

        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        // base_ref_resolved + create_comment + decruft + delete_branch
        Assert.Equal(4, writeEvents.Count);
        Assert.Equal("base_ref_resolved", writeEvents[0].Data["action"].ToString());
        // Default target (no [work] override) is labelled "default" with the base branch.
        Assert.Equal("main", writeEvents[0].Data["target_branch"].ToString());
        Assert.Equal("default", writeEvents[0].Data["source"].ToString());
        Assert.Equal("create_comment", writeEvents[1].Data["action"].ToString());
        Assert.Equal("decruft", writeEvents[2].Data["action"].ToString());
        Assert.Equal("complete", writeEvents[2].Data["halted_at"].ToString());
        Assert.Equal("delete_branch", writeEvents[3].Data["action"].ToString());

        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Single(stateTransitions);
        Assert.Equal("InReview", stateTransitions[0].Data["from"].ToString());
        Assert.Equal("Done", stateTransitions[0].Data["to"].ToString());

        Assert.True(git.DeleteBranchCalls.Count == 1);
        Assert.Equal(BranchName, git.DeleteBranchCalls[0].branch);
        // force:true (-D), not -d: the branch was just fast-forward-merged into the local target,
        // so -d's upstream-merge check (which leaks the branch when origin lags) is inappropriate.
        Assert.True(git.DeleteBranchCalls[0].force);
    }

    [Fact]
    public async Task RunAsync_TargetBranchOverridden_LabelsSourceAsWorkOverrideAndReportsTarget()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var progress = new StringWriter();
        // Override set to the same value as the base branch so the non-default-target
        // preflight (which requires the worktree to be on that branch) is not exercised;
        // this isolates the source-labelling behaviour.
        var shipOptions = MakeShipOptions(targetBranch: "main", targetBranchOverridden: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), shipOptions,
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: decrufter,
            progressWriter: progress);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);

        var baseRefEvent = events.Events.First(e =>
            e.Kind == EventKind.TicketWrite && e.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.Equal("main", baseRefEvent.Data["target_branch"].ToString());
        Assert.Equal("work_override", baseRefEvent.Data["source"].ToString());

        // Operator-facing progress surfaces the resolved target and its source.
        Assert.Contains("[ship] target branch: main (from [work])", progress.ToString());
    }

    [Fact]
    public async Task RunAsync_TicketNotInReview_FailsCleanlyNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.StateCheck, result.FailedAt);
        Assert.Contains("InReview", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(events.Events);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
    }

    [Fact]
    public async Task RunAsync_WorktreeMissing_FailsCleanlyNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: false);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.StateCheck, result.FailedAt);
        Assert.Contains("feature worktree not found", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(events.Events);
        Assert.Equal(0, git.FetchCallCount);
    }

    [Fact]
    public async Task RunAsync_FetchFails_FailsCleanlyNoStateChange()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            FetchResult = new GitOpResult(false, "network down")
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Fetch, result.FailedAt);
        Assert.Contains("network down", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Equal(0, git.RebaseCallCount);
    }

    [Fact]
    public async Task RunAsync_RebaseConflicts_AbortsAndPostsShipBlockedNoStateChange()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            RebaseResult = new RebaseResult(false, true, new[] { "src/A.cs", "src/B.cs" }, "conflicts")
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Rebase, result.FailedAt);
        Assert.Equal(1, git.RebaseAbortCallCount);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked:", ticketing.Comments[0].html);
        Assert.Contains("rebase conflicts in: src/A.cs, src/B.cs", ticketing.Comments[0].html);

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("rebase_conflicts", gates[0].Data["kind"].ToString());
        var paths = (IReadOnlyList<string>)gates[0].Data["conflicting_paths"];
        Assert.Equal(2, paths.Count);
        Assert.Contains("src/A.cs", paths);
        Assert.Contains("src/B.cs", paths);

        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_RebaseAlreadyUpToDate_ProceedsNormally()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        // Simulates "already up to date": Success true, HadConflicts false (the existing FakeGitClient default)
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MergedSha, result.MergedSha);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_ConflictMarkersDetected_PostsShipBlockedNoStateChange()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var hits = new List<ConflictMarkerHit>
        {
            new ConflictMarkerHit("/abs/src/A.cs", 12, "start"),
            new ConflictMarkerHit("/abs/src/A.cs", 18, "end"),
            new ConflictMarkerHit("/abs/src/B.cs", 5, "separator")
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: HitsScanner(hits),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.ConflictMarkerScan, result.FailedAt);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked:", ticketing.Comments[0].html);
        Assert.Contains("conflict markers detected in:", ticketing.Comments[0].html);

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("conflict_markers", gates[0].Data["kind"].ToString());
        var markerFiles = (IReadOnlyList<string>)gates[0].Data["marker_files"];
        Assert.Equal(2, markerFiles.Count);
        Assert.Contains("/abs/src/A.cs", markerFiles);
        Assert.Contains("/abs/src/B.cs", markerFiles);

        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_RegressionChecksFail_PostsShipBlockedNoStateChange()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var checks = new List<CheckResult>
        {
            new CheckResult("build", true, 0, "", "", TimeSpan.Zero),
            new CheckResult("test", false, 1, "", "boom", TimeSpan.Zero),
            new CheckResult("lint", false, 2, "", "style", TimeSpan.Zero)
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(checks),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked:", ticketing.Comments[0].html);
        Assert.Contains("regression checks failed: test, lint", ticketing.Comments[0].html);

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("regression_checks", gates[0].Data["kind"].ToString());
        var checksFailed = (IReadOnlyList<string>)gates[0].Data["checks_failed"];
        Assert.Equal(new[] { "test", "lint" }, checksFailed);

        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_FastForwardMergeFails_FailsCleanlyWithReason()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            FastForwardResult = new GitOpResult(false, "main worktree is on branch 'feature/xyz', not 'main'")
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.FastForwardMerge, result.FailedAt);
        Assert.Contains("feature/xyz", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_HappyPathDeleteFeatureBranchFalse_DoesNotDeleteBranch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(deleteFeatureBranch: false),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(git.DeleteBranchCalls);
        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        Assert.Equal(3, writeEvents.Count); // base_ref_resolved + create_comment + decruft only
        Assert.DoesNotContain(writeEvents, w => w.Data.TryGetValue("action", out var a) && a.ToString() == "delete_branch");
    }

    [Fact]
    public async Task RunAsync_DecruftFailsPostDone_DonePreservedDecruftFailureLogged()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(
            new DecruftResult(DecruftStep.DirectoryDelete,
                new Dictionary<DecruftStep, DecruftStepOutcome>()),
            git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MergedSha, result.MergedSha);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);

        var decruftEvent = events.Events.First(e =>
            e.Kind == EventKind.TicketWrite &&
            e.Data.TryGetValue("action", out var a) && a.ToString() == "decruft");
        Assert.Equal("DirectoryDelete", decruftEvent.Data["halted_at"].ToString());
    }

    [Fact]
    public async Task InterfaceRunAsync_HappyPath_ReturnsPhaseResultWithThreeOutputs()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        IWorkflowPhase iface = phase;
        var result = await iface.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Phase.Ship, result.Phase);
        Assert.Equal(TicketId, result.TicketId);
        Assert.Null(result.FailureReason);
        Assert.Equal(3, result.Outputs.Count);
        Assert.Equal(MergedSha, result.Outputs["merged_sha"]);
        Assert.Equal(BranchName, result.Outputs["branch"]);
        // worktree_path must be the canonical path reported by git worktree list,
        // not the computed layout path (which may double-path when invoked from a worktree).
        Assert.Equal("/some/worktree/path", result.Outputs["worktree_path"]);
    }

    [Fact]
    public async Task InterfaceRunAsync_StateCheckFailure_ReturnsPhaseResultWithEmptyOutputs()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        IWorkflowPhase iface = phase;
        var result = await iface.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(Phase.Ship, result.Phase);
        Assert.Empty(result.Outputs);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_NoRemote_SkipsFetchAndRebasesOntoLocalBaseBranch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            RemoteExistsResult = false
        };
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        // fetch must be skipped
        Assert.Equal(0, git.FetchCallCount);

        // push must also be skipped (no remote)
        Assert.Equal(0, git.PushCallCount);

        // rebase must use local base branch, not remote/baseBranch
        Assert.Equal(1, git.RebaseCallCount);
        Assert.Single(git.RebaseOntoRefs);
        Assert.Equal("main", git.RebaseOntoRefs[0]);

        // ticket transitions to Done and result is success
        Assert.True(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);

        // a fetch_skipped TicketWrite event must appear
        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        var fetchSkipped = writeEvents.FirstOrDefault(e =>
            e.Data.TryGetValue("action", out var a) && a.ToString() == "fetch_skipped");
        Assert.NotNull(fetchSkipped);
        Assert.Equal("no_remote", fetchSkipped!.Data["reason"].ToString());
    }

    [Fact]
    public async Task RunAsync_CanonicalPathFromListWorktrees_UsedForRebaseAndChecks()
    {
        // Arrange: make the git-reported worktree path differ from the computed layout path.
        // The computed path is something like <cwd>/.worktrees/ticket-tlb-1-test-ticket.
        // We configure FakeGitClient to report a completely different canonical path.
        const string canonicalPath = "/canonical/worktree/path";

        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClientWithCanonicalPath(canonicalPath);
        var checksRunner = new RecordingFakeChecksRunner(Array.Empty<CheckResult>());
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: checksRunner,
            markerScanner: EmptyScanner(),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        // Rebase must use the canonical path from git worktree list, not the computed layout path.
        Assert.Equal(canonicalPath, git.LastRebaseFeatureWorktreePath);
        // Checks runner must also use the canonical path.
        Assert.Equal(canonicalPath, checksRunner.LastWorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_FeatureWorktreeDirty_FailsPreFlightBeforeFetch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // The feature worktree canonical path is "/some/worktree/path" (from FakeGitClient.ListWorktreesAsync)
        git.TrackedChangesByPath["/some/worktree/path"] = new[] { " M src/Foo.cs", " M src/Bar.cs" };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains("/some/worktree/path", result.FailureReason ?? "");

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("pre_flight_dirty", gates[0].Data["kind"].ToString());
        var dirtyPaths = (IReadOnlyList<string>)gates[0].Data["dirty_paths"];
        Assert.Contains("/some/worktree/path", dirtyPaths);
    }

    [Fact]
    public async Task RunAsync_MainWorktreeDirty_FailsPreFlightBeforeFetch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Seed the main worktree (workingDirectory = Directory.GetCurrentDirectory()) as dirty
        var workingDir = MakeWorkingDir();
        git.TrackedChangesByPath[workingDir] = new[] { " M README.md" };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, workingDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains(workingDir, result.FailureReason ?? "");

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("pre_flight_dirty", gates[0].Data["kind"].ToString());
        var dirtyPaths = (IReadOnlyList<string>)gates[0].Data["dirty_paths"];
        Assert.Contains(workingDir, dirtyPaths);
    }

    [Fact]
    public async Task RunAsync_ExePathInsideWorktree_FailsPreFlightBeforeFetch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // The feature worktree canonical path is "/some/worktree/path"
        var exePath = "/some/worktree/path/bin/build.exe";
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git),
            processPathProvider: () => exePath);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains(exePath, result.FailureReason ?? "");

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("pre_flight_exe_in_worktree", gates[0].Data["kind"].ToString());
        Assert.Equal(exePath, gates[0].Data["exe_path"].ToString());
    }

    [Fact]
    public async Task RunAsync_ExePathOutsideWorktree_ProceedsNormally()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var exePath = "/usr/local/bin/build.exe";
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git),
            processPathProvider: () => exePath);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MergedSha, result.MergedSha);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_NullExePath_ProceedsNormally()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git),
            processPathProvider: () => null);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MergedSha, result.MergedSha);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_ExePathInSiblingDir_ProceedsNormally()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // The feature worktree is at "/some/worktree/path" but the exe is at
        // "/some/worktree/path-sibling/build.exe" - StartsWith must not match
        var exePath = "/some/worktree/path-sibling/build.exe";
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git),
            processPathProvider: () => exePath);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MergedSha, result.MergedSha);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_LocalMainAheadOfOriginMain_RebasesOntoLocalMain()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: origin/main is ancestor of main (local is ahead)
        git.AncestryResponses[("origin/main", "main")] = true;   // origin/main is ancestor of main
        git.AncestryResponses[("main", "origin/main")] = false;  // main is NOT ancestor of origin/main
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        // Should rebase onto local "main", not origin/main
        Assert.Single(git.RebaseOntoRefs);
        Assert.Equal("main", git.RebaseOntoRefs[0]);

        // Verify base_ref_resolved event with correct reason
        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        var baseRefResolved = writeEvents.FirstOrDefault(w => w.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.NotNull(baseRefResolved);
        Assert.Equal("local_target_ahead", baseRefResolved.Data["reason"].ToString());
    }

    [Fact]
    public async Task RunAsync_DivergedBases_EmitsGateFailureAndBlocksShip()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: bases have diverged (neither is ancestor of the other)
        git.AncestryResponses[("origin/main", "main")] = false;  // origin/main is NOT ancestor of main
        git.AncestryResponses[("main", "origin/main")] = false;  // main is NOT ancestor of origin/main
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("diverged", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Rebase should NOT be attempted
        Assert.Equal(0, git.RebaseCallCount);

        // GateFailure with kind=diverged_bases should be emitted
        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("diverged_bases", gateFailures[0].Data["kind"].ToString());
        Assert.Equal("main", gateFailures[0].Data["local_ref"].ToString());
        Assert.Equal("origin/main", gateFailures[0].Data["remote_ref"].ToString());

        // Ticket should remain in InReview (no Done transition)
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.DoesNotContain(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_RemoteBranchAbsent_RebasesOntoLocalTargetAndShips()
    {
        // Remote is configured but the target branch was never pushed: origin/main does
        // not exist, so the ancestry checks below would both fail. Ship must treat this as
        // "nothing to reconcile", not a divergence (TLB-409).
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            RemoteExistsResult = true,
            RemoteBranchExistsResult = false,
        };
        // Ancestry against the nonexistent ref resolves to neither-ancestor (the real git
        // failure mode), which previously misclassified as diverged.
        git.AncestryResponses[("origin/main", "main")] = false;
        git.AncestryResponses[("main", "origin/main")] = false;
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // No divergence gate failure.
        Assert.DoesNotContain(events.Events, e => e.Kind == EventKind.GateFailure);

        // base_ref resolved onto the local target with the remote_branch_absent reason.
        var baseRefResolved = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(w => w.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.NotNull(baseRefResolved);
        Assert.Equal("remote_branch_absent", baseRefResolved.Data["reason"].ToString());

        // Feature rebase targets the local branch; the first-time push creates the remote branch.
        Assert.Equal("main", git.RebaseOntoRefs.Single());
        Assert.Equal(1, git.PushCallCount);

        // Ship completes: Done transition.
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Contains(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_NoPush_SkipsFetchAndPushAndShipsLocally()
    {
        // Remote is configured, but --no-push / [ship] push=false keeps ship fully local.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true) { RemoteExistsResult = true };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(noPush: true),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // No remote interaction: no fetch, no push.
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.PushCallCount);

        // Rebased onto the local target with the push_disabled reason.
        var baseRefResolved = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(w => w.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.NotNull(baseRefResolved);
        Assert.Equal("push_disabled", baseRefResolved.Data["reason"].ToString());
        Assert.Equal("main", git.RebaseOntoRefs.Single());

        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Contains(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_DivergedNoConflict_AutoRebasesMainAndContinuesShip()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: bases have diverged (neither is ancestor of the other)
        git.AncestryResponses[("origin/main", "main")] = false;
        git.AncestryResponses[("main", "origin/main")] = false;
        // Probe predicts clean rebase
        git.DivergenceStateResult = DivergenceState.DivergedNoConflict;
        // Both rebases (main auto-rebase + feature rebase) succeed with default RebaseResult
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // Two rebases: first onto origin/main (main auto-rebase), second onto main (feature rebase)
        Assert.Equal(2, git.RebaseCallCount);
        Assert.Equal("origin/main", git.RebaseOntoRefs[0]);
        Assert.Equal("main", git.RebaseOntoRefs[1]);

        // MainAutoRebased event emitted with outcome="clean"
        var autoRebased = events.Events.Single(e => e.Kind == EventKind.TargetAutoRebased);
        Assert.Equal("clean", autoRebased.Data["outcome"].ToString());

        // base_ref_resolved with auto_rebased_main reason
        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        var baseRefResolved = writeEvents.FirstOrDefault(w => w.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.NotNull(baseRefResolved);
        Assert.Equal("auto_rebased_target", baseRefResolved.Data["reason"].ToString());

        // Ship completes: Done transition
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Contains(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_DivergedNoConflict_RaceCondition_AbortsRebaseAndEmitsGateFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: bases have diverged
        git.AncestryResponses[("origin/main", "main")] = false;
        git.AncestryResponses[("main", "origin/main")] = false;
        // Probe predicted no conflict but rebase encounters a conflict (race condition)
        git.DivergenceStateResult = DivergenceState.DivergedNoConflict;
        git.RebaseResult = new RebaseResult(false, true, new[] { "src/Foo.cs" }, "conflict");
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Fetch, result.FailedAt);
        Assert.Contains("diverged", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Only one rebase attempted (the main auto-rebase); feature rebase never reached
        Assert.Equal(1, git.RebaseCallCount);

        // Abort called to restore main worktree
        Assert.Equal(1, git.RebaseAbortCallCount);

        // MainAutoRebased event emitted with outcome="raced_to_conflict"
        var autoRebased = events.Events.Single(e => e.Kind == EventKind.TargetAutoRebased);
        Assert.Equal("raced_to_conflict", autoRebased.Data["outcome"].ToString());

        // GateFailure with kind=diverged_bases emitted
        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("diverged_bases", gateFailures[0].Data["kind"].ToString());

        // No Done transition
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.DoesNotContain(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_NoAutoMerge_DivergedNoConflict_SkipsRebaseAndReturnsGateFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: bases have diverged
        git.AncestryResponses[("origin/main", "main")] = false;
        git.AncestryResponses[("main", "origin/main")] = false;
        // Probe predicts no conflict, but --no-auto-merge is set
        git.DivergenceStateResult = DivergenceState.DivergedNoConflict;
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(noAutoMerge: true),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Fetch, result.FailedAt);
        Assert.Contains("diverged", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // No rebase attempted
        Assert.Equal(0, git.RebaseCallCount);

        // No MainAutoRebased event (no attempt was made)
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.TargetAutoRebased));

        // GateFailure with kind=diverged_bases emitted
        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("diverged_bases", gateFailures[0].Data["kind"].ToString());

        // No Done transition
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.DoesNotContain(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_TargetBranchOverride_MainWorktreeOnWrongBranch_BlocksAtPreflight()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            CurrentBranch = "main"
        };

        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(targetBranch: "feature/x"),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.PushCallCount);

        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains("feature/x", ticketing.Comments[0].html);

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("wrong_worktree_branch", gates[0].Data["kind"].ToString());
        Assert.Equal("feature/x", gates[0].Data["expected"].ToString());
        Assert.Equal("main", gates[0].Data["actual"].ToString());
    }

    [Fact]
    public async Task RunAsync_TargetBranchOverride_ShipsToTargetNotBase()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            CurrentBranch = "feature/x"
        };
        // Default ancestry: IsAncestorAsync returns true for any pair not in AncestryResponses,
        // so feature/x and origin/feature/x are treated as the same commit -> ontoRef = origin/feature/x
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(targetBranch: "feature/x"),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // Feature rebase is onto origin/feature/x (same_commit -> remoteRef)
        Assert.Equal(1, git.RebaseCallCount);
        Assert.Equal("origin/feature/x", git.RebaseOntoRefs[0]);

        // Push targeted feature/x, not the base branch (main)
        Assert.Equal(1, git.PushCallCount);
        Assert.Equal("feature/x", git.LastPushedBranch);

        // Done transition still happens
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Contains(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_TargetBranchDiverged_AutoRebasesTargetBranch()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            CurrentBranch = "feature/x"
        };
        // Configure: feature/x and origin/feature/x have diverged
        git.AncestryResponses[("feature/x", "origin/feature/x")] = false;
        git.AncestryResponses[("origin/feature/x", "feature/x")] = false;
        // Probe predicts no conflict
        git.DivergenceStateResult = DivergenceState.DivergedNoConflict;
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(targetBranch: "feature/x"),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);

        // TargetAutoRebased event emitted with outcome="clean"
        var autoRebased = events.Events.Single(e => e.Kind == EventKind.TargetAutoRebased);
        Assert.Equal("clean", autoRebased.Data["outcome"].ToString());

        // base_ref_resolved with auto_rebased_target reason
        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        var baseRefResolved = writeEvents.FirstOrDefault(w => w.Data.TryGetValue("action", out var a) && a.ToString() == "base_ref_resolved");
        Assert.NotNull(baseRefResolved);
        Assert.Equal("auto_rebased_target", baseRefResolved.Data["reason"].ToString());

        // Done transition
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Contains(stateTransitions, t => t.Data["to"].ToString() == "Done");
    }

    [Fact]
    public async Task RunAsync_PushFails_HaltsBeforePostCommentAndDoneTransition()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            PushResult = new GitOpResult(false, "remote: Permission denied")
        };
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Push, result.FailedAt);
        Assert.Contains("remote: Permission denied", result.FailureReason ?? "");

        // ticket must remain InReview - no Done transition, no shipped_at comment
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));

        // FF merge must have succeeded (push comes after)
        Assert.Equal(1, git.FastForwardCallCount);
        Assert.Equal(1, git.PushCallCount);
    }

    // ---------- Fakes ----------

    [Fact]
    public async Task RunAsync_ParentTicket_AllChildrenDone_TransitionsParentToDone()
    {
        var ticket = MakeTicket(TicketState.InReview);
        var ticketing = new FakeTicketing(ticket);
        ticketing.SeedChildren(new[]
        {
            new Ticket("TLB-2", "child-uuid-2", "Child A", "feature", TicketState.Done,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid),
            new Ticket("TLB-3", "child-uuid-3", "Child B", "feature", TicketState.Done,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid)
        });
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.Null(result.MergedSha);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);

        Assert.Single(ticketing.Comments);
        Assert.Contains("shipped", ticketing.Comments[0].html);

        // No git operations should have been invoked
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
    }

    [Fact]
    public async Task RunAsync_ParentTicket_ChildNotDone_ReturnsFailureWithBlockerList()
    {
        var ticket = MakeTicket(TicketState.InReview);
        var ticketing = new FakeTicketing(ticket);
        ticketing.SeedChildren(new[]
        {
            new Ticket("TLB-2", "child-uuid-2", "Child A", "feature", TicketState.Done,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid),
            new Ticket("TLB-3", "child-uuid-3", "Child B", "feature", TicketState.InProgress,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid)
        });
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("TLB-3", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);

        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains("TLB-3", ticketing.Comments[0].html);

        // No git operations
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.RebaseCallCount);
    }

    // -------------------------------------------------------------------------
    // TLB-402: main-worktree detached-HEAD fix
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_B02Fails_NonConflict_AbortsMainRebase()
    {
        // Regression: when B02 auto-rebase fails without conflict markers (e.g., hook
        // rejection), abort was conditional on HadConflicts and so was skipped, leaving
        // the main worktree's HEAD detached. Fix: abort unconditionally on B02 failure.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        // Configure: bases have diverged (neither is ancestor of the other)
        git.AncestryResponses[("origin/main", "main")] = false;
        git.AncestryResponses[("main", "origin/main")] = false;
        // Probe predicts no conflict; rebase fails without conflict markers (e.g., hook rejected)
        git.DivergenceStateResult = DivergenceState.DivergedNoConflict;
        git.RebaseResult = new RebaseResult(false, false, Array.Empty<string>(), "hook-rejected");
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.Fetch, result.FailedAt);

        // Only the B02 auto-rebase was attempted; feature rebase never reached
        Assert.Equal(1, git.RebaseCallCount);

        // Abort called even though HadConflicts is false
        Assert.Equal(1, git.RebaseAbortCallCount);
    }

    [Fact]
    public async Task RunAsync_MainWorktreeDetachedHead_DefaultBranch_BlocksAtPreflight()
    {
        // Regression: the pre-condition branch check was guarded by
        // "if (targetBranch != baseBranch)", so a detached HEAD on the main worktree
        // was invisible when shipping to main (the default). Fix: check is unconditional.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            CurrentBranch = "HEAD" // detached HEAD - git returns literal "HEAD"
        };

        // targetBranch == baseBranch == "main" (default, no override)
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.Equal(0, git.FetchCallCount);
        Assert.Equal(0, git.PushCallCount);

        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
        Assert.Contains("main", ticketing.Comments[0].html); // expected branch present in message

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("wrong_worktree_branch", gates[0].Data["kind"].ToString());
        Assert.Equal("main", gates[0].Data["expected"].ToString());
        Assert.Equal("HEAD", gates[0].Data["actual"].ToString());
    }

    [Fact]
    public async Task RunAsync_PostConditionDetectsDetachedHeadAfterMerge()
    {
        // Regression safety net: after the ff-merge, HEAD is checked inside the lock.
        // If something leaves HEAD detached during merge, ship fails with a clear message.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true)
        {
            CurrentBranch = "main",            // pre-condition passes
            CurrentBranchAfterMerge = "HEAD"   // post-merge state: detached HEAD
        };

        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(), MakeShipOptions(),
            git, checksRunner: new FakeChecksRunner(Array.Empty<CheckResult>()),
            markerScanner: EmptyScanner(),
            decrufter: new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git));

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.FastForwardMerge, result.FailedAt);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("HEAD", result.FailureReason);
        Assert.Contains("main", result.FailureReason);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private List<Ticket> _queryChildren = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedChildren(IReadOnlyList<Ticket> children) => _queryChildren = children.ToList();

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
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(_queryChildren);

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

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly bool _includeWorktreeMatching;
        public GitOpResult FetchResult { get; set; } = new GitOpResult(true, null);
        public RebaseResult RebaseResult { get; set; } = new RebaseResult(true, false, Array.Empty<string>(), null);
        public GitOpResult RebaseAbortResult { get; set; } = new GitOpResult(true, null);
        public GitOpResult FastForwardResult { get; set; } = new GitOpResult(true, null);
        public GitOpResult DeleteBranchResult { get; set; } = new GitOpResult(true, null);
        public bool RemoteExistsResult { get; set; } = true;
        public bool RemoteBranchExistsResult { get; set; } = true;
        public Dictionary<string, IReadOnlyList<string>> TrackedChangesByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string ancestor, string descendant), bool> AncestryResponses { get; } = new();
        public DivergenceState DivergenceStateResult { get; set; } = DivergenceState.DivergedWithConflict;

        public GitOpResult PushResult { get; set; } = new GitOpResult(true, null);
        public int FetchCallCount { get; private set; }
        public int RebaseCallCount { get; private set; }
        public int RebaseAbortCallCount { get; private set; }
        public int FastForwardCallCount { get; private set; }
        public int PushCallCount { get; private set; }
        public string? LastPushedBranch { get; private set; }
        public List<(string branch, bool force)> DeleteBranchCalls { get; } = new();
        public List<string> RebaseOntoRefs { get; } = new();

        public FakeGitClient(bool includeWorktreeMatching)
        {
            _includeWorktreeMatching = includeWorktreeMatching;
        }

        public Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(RemoteExistsResult);

        public Task<bool> RemoteBranchExistsAsync(string remote, string branch, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(RemoteBranchExistsResult);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MergedSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
        {
            if (!_includeWorktreeMatching)
                return Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
            return Task.FromResult<IReadOnlyList<WorktreeInfo>>(new[]
            {
                new WorktreeInfo("/some/worktree/path", BranchName, "deadbeef", false, false)
            });
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public WorktreeCreateResult DetachedWorktreeResult { get; set; } = new WorktreeCreateResult(true, null, null);
        public int CreateDetachedWorktreeCallCount { get; private set; }

        public Task<WorktreeCreateResult> CreateDetachedWorktreeAsync(
            string worktreePath, string sha, string mainWorktreePath, CancellationToken ct)
        {
            CreateDetachedWorktreeCallCount++;
            return Task.FromResult(DetachedWorktreeResult);
        }

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(MergedSha);

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, new[]
            {
                new DiffEntry("src/Foo.cs", DiffKind.Modified, null, 5, 2, null)
            }));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct)
        {
            FetchCallCount++;
            return Task.FromResult(FetchResult);
        }

        public string? LastRebaseFeatureWorktreePath { get; private set; }

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
        {
            RebaseCallCount++;
            RebaseOntoRefs.Add(ontoRef);
            LastRebaseFeatureWorktreePath = featureWorktreePath;
            return Task.FromResult(RebaseResult);
        }

        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct)
        {
            RebaseAbortCallCount++;
            return Task.FromResult(RebaseAbortResult);
        }

        // When non-null, CurrentBranch is updated to this value after FastForwardMergeAsync is called.
        // Lets tests inject a post-merge branch state (e.g., "HEAD" for detached) without affecting
        // the 50+ existing tests that rely on CurrentBranch staying "main" throughout.
        public string? CurrentBranchAfterMerge { get; set; }

        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct)
        {
            FastForwardCallCount++;
            if (CurrentBranchAfterMerge != null)
                CurrentBranch = CurrentBranchAfterMerge;
            return Task.FromResult(FastForwardResult);
        }

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            DeleteBranchCalls.Add((branch, force));
            return Task.FromResult(DeleteBranchResult);
        }

        public Task<GitOpResult> PushAsync(string remote, string branch, string workingDirectory, CancellationToken ct)
        {
            PushCallCount++;
            LastPushedBranch = branch;
            return Task.FromResult(PushResult);
        }

        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
        {
            if (TrackedChangesByPath.TryGetValue(workingDirectory, out var changes))
                return Task.FromResult(changes);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct)
        {
            var key = (ancestor, descendant);
            if (AncestryResponses.TryGetValue(key, out var result))
                return Task.FromResult(result);
            // Default: both are ancestors of each other (same commit)
            return Task.FromResult(true);
        }

        public Task<DivergenceState> ProbeDivergenceAsync(string mainWorktreePath, string baseBranch, string remote, CancellationToken ct) =>
            Task.FromResult(DivergenceStateResult);

        public string CurrentBranch { get; set; } = "main";

        public Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(CurrentBranch);
    }

    private sealed class FakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public FakeChecksRunner(IReadOnlyList<CheckResult> results) { _results = results; }
        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class FakeDecrufter : WorktreeDecrufter
    {
        private readonly DecruftResult _result;
        public FakeDecrufter(DecruftResult result, IGitClient git) : base(git) { _result = result; }
        public override Task<DecruftResult> DecruftAsync(
            string worktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    /// <summary>
    /// A standalone IGitClient that returns a caller-specified canonical worktree path
    /// from ListWorktreesAsync so the canonical-path threading can be verified.
    /// Tracks the last featureWorktreePath argument passed to RebaseAsync.
    /// </summary>
    private sealed class FakeGitClientWithCanonicalPath : IGitClient
    {
        private readonly string _canonicalPath;

        public FakeGitClientWithCanonicalPath(string canonicalPath)
        {
            _canonicalPath = canonicalPath;
        }

        public string? LastRebaseFeatureWorktreePath { get; private set; }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(new[]
            {
                new WorktreeInfo(_canonicalPath, BranchName, "deadbeef", false, false)
            });

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct)
        {
            LastRebaseFeatureWorktreePath = featureWorktreePath;
            return Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));
        }

        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MergedSha);

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(MergedSha);

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct)
        {
            // Default: both are ancestors of each other (same commit)
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// FakeChecksRunner that records the workingDirectory argument for assertion.
    /// </summary>
    private sealed class RecordingFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public string? LastWorkingDirectory { get; private set; }

        public RecordingFakeChecksRunner(IReadOnlyList<CheckResult> results)
        {
            _results = results;
        }

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct)
        {
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(_results);
        }
    }

    // Returns baseline results for worktree paths containing "baseline-" and feature results otherwise.
    private sealed class DirectoryAwareFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _baselineResults;
        private readonly IReadOnlyList<CheckResult> _featureResults;

        public DirectoryAwareFakeChecksRunner(
            IReadOnlyList<CheckResult> baselineResults,
            IReadOnlyList<CheckResult> featureResults)
        {
            _baselineResults = baselineResults;
            _featureResults = featureResults;
        }

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(workingDirectory.Contains("baseline-") ? _baselineResults : _featureResults);
    }

    private static CheckSpec MakeCheckSpec(string name = "check-a", CheckRole role = CheckRole.Gating) =>
        new CheckSpec(name, "dotnet", new[] { "test" }, TimeSpan.FromMinutes(5), Role: role);

    // Records the probe arguments and returns a canned verdict; never touches git or a worktree.
    private sealed class FakeBaselineProber : GateControlProber
    {
        private readonly GateControlVerdict _verdict;
        public int ProbeCallCount { get; private set; }
        public IReadOnlyList<CheckSpec>? LastSpecs { get; private set; }
        public string? LastBaseRef { get; private set; }

        public FakeBaselineProber(GateControlVerdict verdict) { _verdict = verdict; }

        public override Task<GateControlVerdict> ProbeAsync(
            IReadOnlyList<CheckSpec> checks, string baseRef, string mainWorktreePath,
            AutomatedChecksRunner runner, IGitClient git, CancellationToken ct)
        {
            ProbeCallCount++;
            LastSpecs = checks;
            LastBaseRef = baseRef;
            return Task.FromResult(_verdict);
        }
    }

    // ---------- Baseline regression tests ----------

    [Fact]
    public async Task RunAsync_BaselineRegression_FailsShip()
    {
        // Baseline: check-a passes. Feature: check-a fails. -> regression, ship blocked.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", false, 1, "", "test failed", TimeSpan.Zero) };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var baselineCache = new BaselineCache();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: baselineCache),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
        Assert.Contains("regression checks introduced failures", result.FailureReason ?? "");
        Assert.Contains("check-a", result.FailureReason ?? "");

        var gates = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gates);
        Assert.Equal("regression_checks", gates[0].Data["kind"].ToString());
        var regressions = (IReadOnlyList<string>)gates[0].Data["regressions"];
        Assert.Contains("check-a", regressions);

        // Baseline worktree was created
        Assert.Equal(1, git.CreateDetachedWorktreeCallCount);

        // Ship blocked: ticket stays InReview
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);
    }

    [Fact]
    public async Task RunAsync_BaselinePreExisting_ProceedsWithNote()
    {
        // Baseline: check-a fails. Feature: check-a also fails. -> pre-existing, ship proceeds.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", false, 1, "", "pre-existing", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", false, 1, "", "still failing", TimeSpan.Zero) };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var baselineCache = new BaselineCache();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: baselineCache),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // pre_existing_failures_noted event emitted
        var preExistingEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "pre_existing_failures_noted");
        Assert.NotNull(preExistingEvent);
        var names = (IReadOnlyList<string>)preExistingEvent.Data["names"];
        Assert.Contains("check-a", names);

        // No gate failure
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));

        // Done transition
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_BaselineFix_ProceedsWithNote()
    {
        // Baseline: check-a fails. Feature: check-a passes. -> fix, ship proceeds.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", false, 1, "", "broken in baseline", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var baselineCache = new BaselineCache();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: baselineCache),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        // fixes_detected event emitted
        var fixesEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "fixes_detected");
        Assert.NotNull(fixesEvent);
        var names = (IReadOnlyList<string>)fixesEvent.Data["names"];
        Assert.Contains("check-a", names);

        // No gate failure
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));

        // Done transition
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_SkipBaseline_LegacyBehavior()
    {
        // SkipBaseline=true: any failing test blocks ship regardless of baseline, baseline_skipped emitted.
        var checkSpec = MakeCheckSpec();
        var featureResults = new CheckResult[] { new("check-a", false, 1, "", "test failed", TimeSpan.Zero) };
        var checksRunner = new FakeChecksRunner(featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, skipBaseline: true),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);

        // baseline_skipped event must be present
        var skippedEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "baseline_skipped");
        Assert.NotNull(skippedEvent);

        // Legacy gate failure uses checks_failed field
        var gate = events.Events.First(e => e.Kind == EventKind.GateFailure);
        Assert.Equal("regression_checks", gate.Data["kind"].ToString());
        Assert.True(gate.Data.ContainsKey("checks_failed"));

        // No baseline worktree creation
        Assert.Equal(0, git.CreateDetachedWorktreeCallCount);
    }

    [Fact]
    public async Task RunAsync_BaselineCacheReuse_ComputedOnce()
    {
        // Two ship invocations sharing the same BaselineCache and same SHA:
        // baseline worktree is created exactly once (second ship hits the cache).
        var checkSpec = MakeCheckSpec();
        var baselineResults = Array.Empty<CheckResult>();
        var featureResults = Array.Empty<CheckResult>();
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var baselineCache = new BaselineCache();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var shipOptions = MakeShipOptions(checks: new[] { checkSpec }, baselineCache: baselineCache);

        // Ship 1
        var ticketing1 = new FakeTicketing(MakeTicket(TicketState.InReview));
        var phase1 = new ShipPhase(ticketing1, new FakeEventSink(), MakeBuildOptions(), shipOptions,
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);
        var result1 = await phase1.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);
        Assert.True(result1.Success);
        Assert.Equal(1, git.CreateDetachedWorktreeCallCount);

        // Ship 2 with the same cache and same SHA - no new worktree creation
        var ticketing2 = new FakeTicketing(MakeTicket(TicketState.InReview));
        var phase2 = new ShipPhase(ticketing2, new FakeEventSink(), MakeBuildOptions(), shipOptions,
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);
        var result2 = await phase2.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);
        Assert.True(result2.Success);
        Assert.Equal(1, git.CreateDetachedWorktreeCallCount); // still 1, not 2
    }

    // ---------- Advisory-role regression tests ----------

    [Fact]
    public async Task RunAsync_AdvisoryRegression_NeverBlocksShip()
    {
        // Baseline: lint (advisory) passes. Feature: lint fails. Advisory checks are documented
        // as never hard-failing; the ship gate must honor that like the review gate does.
        var checkSpec = MakeCheckSpec("lint", CheckRole.Advisory);
        var baselineResults = new CheckResult[] { new("lint", true, 0, "", "", TimeSpan.Zero, CheckRole.Advisory) };
        var featureResults = new CheckResult[] { new("lint", false, 2, "2 violations", "", TimeSpan.Zero, CheckRole.Advisory) };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);

        var advisoryEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "advisory_regressions_noted");
        Assert.NotNull(advisoryEvent);
        var names = (IReadOnlyList<string>)advisoryEvent.Data["names"];
        Assert.Contains("lint", names);

        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_AdvisoryAndGatingRegressions_BlocksOnGatingOnly()
    {
        // Feature breaks both a gating check and an advisory check: ship blocks on the gating
        // regression only, and the gate-failure event reports the advisory one separately.
        var specs = new[] { MakeCheckSpec("test"), MakeCheckSpec("lint", CheckRole.Advisory) };
        var baselineResults = new CheckResult[]
        {
            new("test", true, 0, "", "", TimeSpan.Zero),
            new("lint", true, 0, "", "", TimeSpan.Zero, CheckRole.Advisory)
        };
        var featureResults = new CheckResult[]
        {
            new("test", false, 1, "", "test failed", TimeSpan.Zero),
            new("lint", false, 2, "2 violations", "", TimeSpan.Zero, CheckRole.Advisory)
        };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: specs, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
        Assert.Contains("test", result.FailureReason ?? "");
        Assert.DoesNotContain("lint", result.FailureReason ?? "");

        var gate = events.Events.Single(e => e.Kind == EventKind.GateFailure);
        var regressions = (IReadOnlyList<string>)gate.Data["regressions"];
        Assert.Equal(new[] { "test" }, regressions);
        var advisoryRegressions = (IReadOnlyList<string>)gate.Data["advisory_regressions"];
        Assert.Equal(new[] { "lint" }, advisoryRegressions);
    }

    [Fact]
    public async Task RunAsync_LegacyAdvisoryFailure_DoesNotBlock()
    {
        // SkipBaseline (legacy path): an advisory failure is noted but never blocks ship.
        var checkSpec = MakeCheckSpec("lint", CheckRole.Advisory);
        var featureResults = new CheckResult[] { new("lint", false, 2, "2 violations", "", TimeSpan.Zero, CheckRole.Advisory) };
        var checksRunner = new FakeChecksRunner(featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, skipBaseline: true),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        var advisoryEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .FirstOrDefault(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "advisory_failures_noted");
        Assert.NotNull(advisoryEvent);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    // ---------- Baseline contradiction re-check tests ----------

    [Fact]
    public async Task RunAsync_BaselineRecheck_ConfirmedOnBase_ReclassifiesAndShips()
    {
        // The baseline run reported check-a clean (poisoned, e.g. by a user-global linter cache),
        // the feature branch fails it. The re-check probe finds check-a failing on the pristine
        // base too: reclassify as pre-existing, correct the cache entry, and ship.
        var setupSpec = MakeCheckSpec("prep", CheckRole.Setup);
        var failingSpec = MakeCheckSpec("check-a");
        var passingSpec = MakeCheckSpec("check-b");
        var specs = new[] { setupSpec, failingSpec, passingSpec };
        var baselineResults = new CheckResult[]
        {
            new("prep", true, 0, "", "", TimeSpan.Zero, CheckRole.Setup),
            new("check-a", true, 0, "", "", TimeSpan.Zero),
            new("check-b", true, 0, "", "", TimeSpan.Zero)
        };
        var featureResults = new CheckResult[]
        {
            new("prep", true, 0, "", "", TimeSpan.Zero, CheckRole.Setup),
            new("check-a", false, 2, "2 violations", "", TimeSpan.Zero),
            new("check-b", true, 0, "", "", TimeSpan.Zero)
        };
        var prober = new FakeBaselineProber(new GateControlVerdict(
            GateControlOutcome.BaseFails, MergedSha, new CheckResult[]
            {
                new("prep", true, 0, "", "", TimeSpan.Zero, CheckRole.Setup),
                new("check-a", false, 2, "2 violations", "", TimeSpan.Zero)
            }));
        var baselineCache = new BaselineCache();
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: specs, baselineCache: baselineCache),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter,
            baselineProber: prober);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));

        // The probe re-ran only the regressed check plus Setup prerequisites.
        Assert.Equal(1, prober.ProbeCallCount);
        Assert.Equal(new[] { "prep", "check-a" }, prober.LastSpecs!.Select(s => s.Name).ToArray());
        Assert.Equal(MergedSha, prober.LastBaseRef);

        var recheckEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .Single(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "baseline_recheck");
        var confirmed = (IReadOnlyList<string>)recheckEvent.Data["confirmed_failing_on_base"];
        Assert.Equal(new[] { "check-a" }, confirmed);

        // Reclassified as pre-existing.
        var preExistingEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .Single(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "pre_existing_failures_noted");
        Assert.Contains("check-a", (IReadOnlyList<string>)preExistingEvent.Data["names"]);

        // The poisoned cache entry was corrected so later ships in the chain inherit it.
        Assert.True(baselineCache.TryGet(MergedSha, out var corrected));
        Assert.Contains("check-a", corrected);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_BaselineRecheck_BasePasses_StillBlocks()
    {
        // The re-check confirms the base really is clean: the regression stands, ship blocks.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", false, 1, "", "test failed", TimeSpan.Zero) };
        var prober = new FakeBaselineProber(new GateControlVerdict(
            GateControlOutcome.BasePasses, MergedSha,
            new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) }));
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter,
            baselineProber: prober);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
        Assert.Equal(1, prober.ProbeCallCount);
        var gate = events.Events.Single(e => e.Kind == EventKind.GateFailure);
        Assert.Contains("check-a", (IReadOnlyList<string>)gate.Data["regressions"]);
    }

    [Fact]
    public async Task RunAsync_BaselineRecheck_Inconclusive_StillBlocks()
    {
        // An inconclusive probe must never downgrade a regression to pre-existing.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", false, 1, "", "test failed", TimeSpan.Zero) };
        var prober = new FakeBaselineProber(new GateControlVerdict(
            GateControlOutcome.Inconclusive, null, Array.Empty<CheckResult>(), "worktree creation failed"));
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter,
            baselineProber: prober);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
    }

    [Fact]
    public async Task RunAsync_BaselineRecheck_SetupFailedOnBase_StillBlocks()
    {
        // A failed Setup step on the pristine base means the control results prove nothing
        // about the regressed check; the regression classification must stand.
        var setupSpec = MakeCheckSpec("prep", CheckRole.Setup);
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[]
        {
            new("prep", true, 0, "", "", TimeSpan.Zero, CheckRole.Setup),
            new("check-a", true, 0, "", "", TimeSpan.Zero)
        };
        var featureResults = new CheckResult[]
        {
            new("prep", true, 0, "", "", TimeSpan.Zero, CheckRole.Setup),
            new("check-a", false, 1, "", "test failed", TimeSpan.Zero)
        };
        var prober = new FakeBaselineProber(new GateControlVerdict(
            GateControlOutcome.BaseFails, MergedSha, new CheckResult[]
            {
                new("prep", false, 1, "", "install failed", TimeSpan.Zero, CheckRole.Setup),
                new("check-a", false, 1, "", "cannot run", TimeSpan.Zero)
            }));
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { setupSpec, checkSpec }, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter,
            baselineProber: prober);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.RegressionChecks, result.FailedAt);
        var recheckEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .Single(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "baseline_recheck");
        Assert.Empty((IReadOnlyList<string>)recheckEvent.Data["confirmed_failing_on_base"]);
    }

    [Fact]
    public async Task RunAsync_BaselineComputed_EmitsPerCheckEvidence()
    {
        // The baseline_computed event must carry per-check evidence (name, exit code, output
        // tails) so a wrong baseline is diagnosable from the event log alone.
        var checkSpec = MakeCheckSpec();
        var baselineResults = new CheckResult[] { new("check-a", false, 2, "2 violations found", "warn", TimeSpan.Zero) };
        var featureResults = new CheckResult[] { new("check-a", true, 0, "", "", TimeSpan.Zero) };
        var checksRunner = new DirectoryAwareFakeChecksRunner(baselineResults, featureResults);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var events = new FakeEventSink();
        var git = new FakeGitClient(includeWorktreeMatching: true);
        var decrufter = new FakeDecrufter(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()), git);
        var phase = new ShipPhase(ticketing, events, MakeBuildOptions(),
            MakeShipOptions(checks: new[] { checkSpec }, baselineCache: new BaselineCache()),
            git, checksRunner: checksRunner, markerScanner: EmptyScanner(), decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        var computedEvent = events.Events
            .Where(e => e.Kind == EventKind.TicketWrite)
            .Single(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "baseline_computed");
        Assert.Equal(1, (int)computedEvent.Data["failing_count"]);
        Assert.Contains("check-a", (IReadOnlyList<string>)computedEvent.Data["failing"]);
        var evidence = (List<Dictionary<string, object>>)computedEvent.Data["check_evidence"];
        var entry = Assert.Single(evidence);
        Assert.Equal("check-a", entry["name"]);
        Assert.Equal(false, entry["passed"]);
        Assert.Equal(2, (int)entry["exit_code"]);
        Assert.Equal("2 violations found", entry["stdout_tail"]);
        Assert.Equal("warn", entry["stderr_tail"]);
    }
}
