using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the working-tree hygiene gate (Brief 15 of Plan D).
///
/// Three scenarios:
///   - A phase started on a conflicted tree stops immediately and names the files.
///   - A dangling stash from an unrelated ticket is detected and reported.
///   - A clean tree passes the gate with no behavior change.
///
/// All tests use hermetic fakes: no real repository, no real git process.
/// </summary>
public class HygieneGateTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";
    private const string TicketId = "TLB-1";
    private const string TicketBranchPrefix = "ticket/tlb-1-";

    private static Ticket MakeReadyTicket() => new Ticket(
        Id: TicketId,
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static Ticket MakeInReviewTicket() => new Ticket(
        Id: TicketId,
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.InReview,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-hygiene",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static ShipOptions MakeShipOptions() => new ShipOptions(
        RegressionChecks: Array.Empty<CheckSpec>(),
        Remote: "origin",
        BaseBranch: "main",
        DeleteFeatureBranch: false);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "ok", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object> { ["commit_sha"] = CommitSha });

    // -----------------------------------------------------------------------
    // ImplementPhase hygiene gate tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Implement_ConflictedTree_StopsImmediatelyAndNamesFiles()
    {
        var conflictedFiles = new[] { "src/A.cs", "src/B.cs" };
        var git = new FakeGitHygiene(conflictedPaths: conflictedFiles, stashEntries: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeReadyTicket());
        var worker = new FakeWorkerHygiene(OkWorkerResult());
        var events = new FakeEventSinkHygiene();
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        // Gate must block
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("src/A.cs", result.FailureReason);
        Assert.Contains("src/B.cs", result.FailureReason);

        // No worker, no transitions
        Assert.False(worker.WasInvoked);
        Assert.Empty(ticketing.Transitions);

        // GateFailure event with kind=hygiene_gate
        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("hygiene_gate", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Implement_DanglingUnrelatedStash_StopsAndReportsStash()
    {
        var stashLines = new[]
        {
            "stash@{0}: On ticket/tlb-999-other-ticket: WIP on other work",
            "stash@{1}: WIP on main: abc1234 something unrelated"
        };
        var git = new FakeGitHygiene(conflictedPaths: Array.Empty<string>(), stashEntries: stashLines);
        var ticketing = new FakeTicketingHygiene(MakeReadyTicket());
        var worker = new FakeWorkerHygiene(OkWorkerResult());
        var events = new FakeEventSinkHygiene();
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        // Gate must block
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("dangling stash", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        // The stash entry text should appear in the failure reason or detail
        var detail = result.FailureReason!;
        Assert.True(
            detail.Contains("stash@{0}") || detail.Contains("stash@{1}"),
            $"Expected stash entry reference in failure reason, got: {detail}");

        // No worker, no transitions
        Assert.False(worker.WasInvoked);
        Assert.Empty(ticketing.Transitions);

        // GateFailure event with kind=hygiene_gate
        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("hygiene_gate", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Implement_CleanTree_PassesGateAndProceedsNormally()
    {
        var git = new FakeGitHygiene(conflictedPaths: Array.Empty<string>(), stashEntries: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeReadyTicket());
        var worker = new FakeWorkerHygiene(OkWorkerResult());
        var events = new FakeEventSinkHygiene();
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        // Gate passes - phase continues to completion
        Assert.True(result.Success);
        Assert.True(worker.WasInvoked);

        // No hygiene gate event
        Assert.DoesNotContain(events.Events, e =>
            e.Kind == EventKind.GateFailure &&
            e.Data.TryGetValue("kind", out var k) &&
            k.ToString() == "hygiene_gate");
    }

    [Fact]
    public async Task Implement_StashBelongingToCurrentTicket_PassesGate()
    {
        // A stash that mentions the current ticket branch prefix is considered "related"
        // and must not block the gate.
        var stashLines = new[]
        {
            $"stash@{{0}}: On {TicketBranchPrefix}test-ticket: WIP for this ticket"
        };
        var git = new FakeGitHygiene(conflictedPaths: Array.Empty<string>(), stashEntries: stashLines);
        var ticketing = new FakeTicketingHygiene(MakeReadyTicket());
        var worker = new FakeWorkerHygiene(OkWorkerResult());
        var events = new FakeEventSinkHygiene();
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(worker.WasInvoked);
    }

    // -----------------------------------------------------------------------
    // ShipPhase preflight hygiene gate tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ship_ConflictedTree_StopsAtPreflightAndNamesFiles()
    {
        var conflictedFiles = new[] { "src/Conflict.cs" };
        var git = new FakeGitHygieneForShip(conflictedPaths: conflictedFiles, stashEntries: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("src/Conflict.cs", result.FailureReason);

        // ship_blocked comment posted
        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);

        // No state transition
        Assert.Empty(ticketing.Transitions);

        // GateFailure event with kind=pre_flight_hygiene
        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("pre_flight_hygiene", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Ship_UnrelatedStashPresent_StopsAtPreflightAndReportsStash()
    {
        var stashLines = new[]
        {
            "stash@{0}: On ticket/tlb-888-unrelated: some WIP"
        };
        var git = new FakeGitHygieneForShip(conflictedPaths: Array.Empty<string>(), stashEntries: stashLines);
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("stash", result.FailureReason, StringComparison.OrdinalIgnoreCase);

        Assert.Single(ticketing.Comments);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);

        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("pre_flight_hygiene", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Ship_CleanTree_PassesPreflightAndShips()
    {
        var git = new FakeGitHygieneForShip(conflictedPaths: Array.Empty<string>(), stashEntries: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailedAt);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Done, ticketing.Transitions[0].state);

        // No hygiene gate failure
        Assert.DoesNotContain(events.Events, e =>
            e.Kind == EventKind.GateFailure &&
            e.Data.TryGetValue("kind", out var k) &&
            k.ToString() == "pre_flight_hygiene");
    }

    // -----------------------------------------------------------------------
    // Untracked-file collision tests (TLB-489)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ship_UntrackedInFeatureCollidesWithMainTracked_StopsAtPreflightNamesFile()
    {
        // Feature worktree has an untracked .build/_ticket-foo.md that is tracked in main.
        // Ship must block at preflight and name the colliding file.
        var untrackedInFeature = new[] { ".build/_ticket-foo.md" };
        var trackedInMain = new[] { ".build/_ticket-foo.md" };
        var git = new FakeGitHygieneForShipWithUntracked(
            conflictedPaths: Array.Empty<string>(),
            stashEntries: Array.Empty<string>(),
            featureUntrackedFiles: untrackedInFeature,
            mainTrackedSubset: trackedInMain,
            mainUntrackedFiles: Array.Empty<string>(),
            featureTrackedSubset: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(".build/_ticket-foo.md", result.FailureReason);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);

        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("pre_flight_hygiene", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Ship_UntrackedInMainCollidesWithFeatureTracked_StopsAtPreflightNamesFile()
    {
        // Main worktree has an untracked file that is tracked in the feature branch.
        // Ship must block at preflight before the FF merge starts.
        var untrackedInMain = new[] { "src/NewFile.cs" };
        var trackedInFeature = new[] { "src/NewFile.cs" };
        var git = new FakeGitHygieneForShipWithUntracked(
            conflictedPaths: Array.Empty<string>(),
            stashEntries: Array.Empty<string>(),
            featureUntrackedFiles: Array.Empty<string>(),
            mainTrackedSubset: Array.Empty<string>(),
            mainUntrackedFiles: untrackedInMain,
            featureTrackedSubset: trackedInFeature);
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ShipFailureStage.PreFlight, result.FailedAt);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("src/NewFile.cs", result.FailureReason);
        Assert.Contains("ship_blocked", ticketing.Comments[0].html);

        var gateEvents = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateEvents);
        Assert.Equal("pre_flight_hygiene", gateEvents[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task Ship_UntrackedFileNoCollision_PassesPreflight()
    {
        // Feature worktree has an untracked file, but it is NOT tracked in main.
        // No collision -> preflight passes and ship succeeds.
        var untrackedInFeature = new[] { ".build/brief.md" };
        var git = new FakeGitHygieneForShipWithUntracked(
            conflictedPaths: Array.Empty<string>(),
            stashEntries: Array.Empty<string>(),
            featureUntrackedFiles: untrackedInFeature,
            mainTrackedSubset: Array.Empty<string>(),   // not tracked in main
            mainUntrackedFiles: Array.Empty<string>(),
            featureTrackedSubset: Array.Empty<string>());
        var ticketing = new FakeTicketingHygiene(MakeInReviewTicket());
        var events = new FakeEventSinkHygiene();
        var decrufter = new FakeDecrufterHygiene(git);
        var phase = new ShipPhase(ticketing, events, MakeOptions(), MakeShipOptions(),
            git,
            checksRunner: new FakeChecksRunnerHygiene(Array.Empty<CheckResult>()),
            markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
            decrufter: decrufter);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(events.Events, e =>
            e.Kind == EventKind.GateFailure &&
            e.Data.TryGetValue("kind", out var k) &&
            k.ToString() == "pre_flight_hygiene");
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Minimal git fake for ImplementPhase hygiene tests.
    /// Returns the supplied conflicted paths and stash entries; all other
    /// methods return benign defaults so the phase can proceed normally
    /// when the gate passes.
    /// </summary>
    private sealed class FakeGitHygiene : IGitClient
    {
        private readonly IReadOnlyList<string> _conflictedPaths;
        private readonly IReadOnlyList<string> _stashEntries;
        private readonly string _mainSha;
        private readonly string _headSha;

        public FakeGitHygiene(
            IReadOnlyList<string> conflictedPaths,
            IReadOnlyList<string> stashEntries,
            string? mainSha = null,
            string? headSha = null)
        {
            _conflictedPaths = conflictedPaths;
            _stashEntries = stashEntries;
            _mainSha = mainSha ?? MainSha;
            _headSha = headSha ?? CommitSha;
        }

        public Task<IReadOnlyList<string>> GetConflictedPathsAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_conflictedPaths);

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_stashEntries);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(_headSha);

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
    }

    /// <summary>
    /// Git fake for ShipPhase hygiene tests. Provides a worktree list so the
    /// phase finds the feature worktree, plus controllable conflict and stash state.
    /// </summary>
    private sealed class FakeGitHygieneForShip : IGitClient
    {
        private const string BranchName = "ticket/tlb-1-test-ticket";
        private const string WorktreePath = "/ship/worktree/path";

        private readonly IReadOnlyList<string> _conflictedPaths;
        private readonly IReadOnlyList<string> _stashEntries;

        public FakeGitHygieneForShip(
            IReadOnlyList<string> conflictedPaths,
            IReadOnlyList<string> stashEntries)
        {
            _conflictedPaths = conflictedPaths;
            _stashEntries = stashEntries;
        }

        public Task<IReadOnlyList<string>> GetConflictedPathsAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_conflictedPaths);

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_stashEntries);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(new[]
            {
                new WorktreeInfo(WorktreePath, BranchName, "deadbeef", false, false)
            });

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(MainSha);

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

        public Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(false); // skip fetch to keep the test focused on hygiene

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Git fake for untracked-file collision tests (TLB-489). Extends FakeGitHygieneForShip
    /// with controllable untracked-file and filter-tracked responses for both worktrees.
    /// </summary>
    private sealed class FakeGitHygieneForShipWithUntracked : IGitClient
    {
        private const string BranchName = "ticket/tlb-1-test-ticket";
        private const string WorktreePath = "/ship/worktree/path";

        private readonly IReadOnlyList<string> _conflictedPaths;
        private readonly IReadOnlyList<string> _stashEntries;
        private readonly IReadOnlyList<string> _featureUntrackedFiles;
        private readonly IReadOnlyList<string> _mainTrackedSubset;
        private readonly IReadOnlyList<string> _mainUntrackedFiles;
        private readonly IReadOnlyList<string> _featureTrackedSubset;

        public FakeGitHygieneForShipWithUntracked(
            IReadOnlyList<string> conflictedPaths,
            IReadOnlyList<string> stashEntries,
            IReadOnlyList<string> featureUntrackedFiles,
            IReadOnlyList<string> mainTrackedSubset,
            IReadOnlyList<string> mainUntrackedFiles,
            IReadOnlyList<string> featureTrackedSubset)
        {
            _conflictedPaths = conflictedPaths;
            _stashEntries = stashEntries;
            _featureUntrackedFiles = featureUntrackedFiles;
            _mainTrackedSubset = mainTrackedSubset;
            _mainUntrackedFiles = mainUntrackedFiles;
            _featureTrackedSubset = featureTrackedSubset;
        }

        public Task<IReadOnlyList<string>> GetConflictedPathsAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_conflictedPaths);

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_stashEntries);

        public Task<IReadOnlyList<string>> GetUntrackedFilesAsync(string workingDirectory, CancellationToken ct)
        {
            // Feature worktree has its own untracked set; main worktree has its own.
            var result = workingDirectory == WorktreePath ? _featureUntrackedFiles : _mainUntrackedFiles;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<string>> FilterTrackedPathsAsync(IReadOnlyList<string> paths, string workingDirectory, CancellationToken ct)
        {
            // When checking against the main worktree, return mainTrackedSubset filtered to paths asked about.
            // When checking against the feature worktree, return featureTrackedSubset filtered to paths asked about.
            var source = workingDirectory == WorktreePath ? _featureTrackedSubset : _mainTrackedSubset;
            var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> filtered = source.Where(p => pathSet.Contains(p)).ToList();
            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(new[]
            {
                new WorktreeInfo(WorktreePath, BranchName, "deadbeef", false, false)
            });

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(MainSha);

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

        public Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(false); // skip fetch to keep test focused on hygiene

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class FakeTicketingHygiene : ITicketing
    {
        private readonly Ticket _ticket;

        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketingHygiene(Ticket ticket) { _ticket = ticket; }

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
            return Task.FromResult("comment-hygiene-1");
        }
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Relation>)Array.Empty<Relation>());
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
    
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

    public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class FakeWorkerHygiene : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public bool WasInvoked { get; private set; }
        public FakeWorkerHygiene(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeEventSinkHygiene : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) { Events.Add(ev); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeChecksRunnerHygiene : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public FakeChecksRunnerHygiene(IReadOnlyList<CheckResult> results) { _results = results; }
        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class FakeDecrufterHygiene : WorktreeDecrufter
    {
        public FakeDecrufterHygiene(IGitClient git) : base(git) { }
        public override Task<DecruftResult> DecruftAsync(string worktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }
}
