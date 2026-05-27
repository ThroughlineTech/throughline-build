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
    private const string BranchName = "ticket/tlb-1-test-ticket";
    private const string MergedSha = "0123456789abcdef0123456789abcdef01234567";

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

    private static BuildOptions MakeBuildOptions() => new BuildOptions(
        SessionId: "session-1",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static ShipOptions MakeShipOptions(
        IReadOnlyList<CheckSpec>? checks = null,
        bool deleteFeatureBranch = true) => new ShipOptions(
            RegressionChecks: checks ?? Array.Empty<CheckSpec>(),
            Remote: "origin",
            BaseBranch: "main",
            DeleteFeatureBranch: deleteFeatureBranch);

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
        Assert.False(git.DeleteBranchCalls[0].force);
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
        Assert.Equal("local_main_ahead", baseRefResolved.Data["reason"].ToString());
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

    // ---------- Fakes ----------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

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
        public Dictionary<string, IReadOnlyList<string>> TrackedChangesByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string ancestor, string descendant), bool> AncestryResponses { get; } = new();

        public int FetchCallCount { get; private set; }
        public int RebaseCallCount { get; private set; }
        public int RebaseAbortCallCount { get; private set; }
        public int FastForwardCallCount { get; private set; }
        public List<(string branch, bool force)> DeleteBranchCalls { get; } = new();
        public List<string> RebaseOntoRefs { get; } = new();

        public FakeGitClient(bool includeWorktreeMatching)
        {
            _includeWorktreeMatching = includeWorktreeMatching;
        }

        public Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(RemoteExistsResult);

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

        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct)
        {
            FastForwardCallCount++;
            return Task.FromResult(FastForwardResult);
        }

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            DeleteBranchCalls.Add((branch, force));
            return Task.FromResult(DeleteBranchResult);
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
}
