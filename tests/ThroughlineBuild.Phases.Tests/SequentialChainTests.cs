using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the sequential-chain behaviors added by Briefs 04-06:
///   (1) A multi-ticket parent chain dispatches in dependency order (width-1, blocked_by respected).
///   (2) Ancestor-skip fires when an upstream ticket fails and the dispatcher exit-code mapping
///       is unchanged (success=false, failure reason populated, no further levels dispatched).
///   (3) A multi-ticket parent chain creates and removes exactly one worktree for its entire run.
///
/// All tests are hermetic: no real repository, no real git process.
/// </summary>
public class SequentialChainTests
{
    // ----- shared constants -----

    private const string ParentId = "TLB-1";
    private const string ParentUuid = "parent-uuid-1";
    private const string ParentTitle = "Parent ticket";
    private const string Child1Id = "TLB-2";
    private const string Child1Uuid = "child-uuid-1";
    private const string Child2Id = "TLB-3";
    private const string Child2Uuid = "child-uuid-2";
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

    private static IReadOnlyDictionary<string, object> OkWorkerMeta() =>
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "s",
            ["planned_at_sha"] = MainSha,
            ["files_changed"] = Array.Empty<string>()
        };

    private static IReadOnlyDictionary<string, string> OkWorkerBlocks() =>
        new Dictionary<string, string> { ["PLAN_BODY"] = "# Plan\nThis is the plan." };

    private static Ticket MakeParent() => new Ticket(
        Id: ParentId,
        Uuid: ParentUuid,
        Title: ParentTitle,
        Type: "feature",
        State: TicketState.Backlog,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>parent</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static Ticket MakeChild(string id, string uuid, TicketState state) => new Ticket(
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
        ParentId: ParentUuid);

    private int _sessionCounter;
    private string NextSessionId() => $"session-{++_sessionCounter}";

    // ----- BUILD HELPERS -----

    /// <summary>
    /// Builds a ChainPhase wired to the supplied fakes. Each child ticket goes through
    /// plan -> implement -> review (pass) -> ship when starting from Backlog.
    /// The <paramref name="git"/> fake is shared across all phases so worktree
    /// create/remove calls are centrally tracked.
    /// </summary>
    private ChainPhase BuildChain(
        SeqFakeTicketing ticketing,
        Queue<IVerifier> verifierQueue,
        SeqFakeGitClient git,
        bool planWorkerFails = false)
    {
        _sessionCounter = 0;
        var events = new SeqFakeEventSink();
        var baseOpts = MakeBaseOptions();

        var planWorker = planWorkerFails
            ? (IWorkerAgent)new SeqFailWorkerAgent()
            : new SeqOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());

        var implWorker = new SeqOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, events, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, ReviewPhase> reviewFactory = opts =>
        {
            var verifier = verifierQueue.Dequeue();
            return new ReviewPhase(ticketing, new SeqOkWorkerAgent(null, null), events, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        // Standard ship factory (decruft allowed) - used for single-ticket chains.
        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new SeqFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new SeqFakeDecrufter(git),
                processPathProvider: () => null);

        // Chain ship factory (SkipDecruft=true) - used by ChainPhase for parent-chain children
        // so the shared worktree is not removed after each ticket.
        Func<BuildOptions, ShipPhase> chainShipFactory = opts =>
            new ShipPhase(ticketing, events, opts,
                MakeShipOptions() with { SkipDecruft = true }, git,
                checksRunner: new SeqFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new SeqFakeDecrufter(git),
                processPathProvider: () => null);

        return new ChainPhase(
            ticketing, events, baseOpts,
            planFactory, implFactory, reviewFactory, shipFactory,
            sessionIdGenerator: NextSessionId,
            workingDirectory: WorkDir,
            ratifierFactory: null,
            chainShipFactory: chainShipFactory,
            gitClient: git);
    }

    // ==========================================================================
    // Test 1: multi-ticket parent chain dispatches sequentially in dependency order
    // ==========================================================================

    [Fact]
    public async Task ParentChain_DependentChildren_ExecutesInDependencyOrder()
    {
        // Arrange: parent with two children where child2 (TLB-3) is blocked_by child1 (TLB-2).
        // Levels from topological sort: [[TLB-2], [TLB-3]].
        // Width-1 dispatch means child1 must complete before child2 starts.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });
        // TLB-3 blocked_by TLB-2 -> TLB-2 must run first.
        ticketing.SeedRelations(Child2Id, new[] { new Relation("blocked_by", Child1Id) });

        var verifiers = new Queue<IVerifier>();
        // Each child gets one pass verdict.
        verifiers.Enqueue(new SeqPassVerifier());
        verifiers.Enqueue(new SeqPassVerifier());

        var git = new SeqFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        // Act
        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Assert
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);

        // Dependency order respected: TLB-2 (level 0) before TLB-3 (level 1).
        Assert.Equal(Child1Id, result.ChildResults[0].TicketId);
        Assert.Equal(Child2Id, result.ChildResults[1].TicketId);

        // Both children completed successfully.
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    [Fact]
    public async Task ParentChain_DependentChildren_IndependentSiblingsPreserveInputOrder()
    {
        // Arrange: two independent siblings (no relation between them).
        // With width-1 dispatch they still both complete; input order is preserved
        // within the single level returned by topological sort.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });
        // No relations -> both in level 0, input order preserved.

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new SeqPassVerifier());
        verifiers.Enqueue(new SeqPassVerifier());

        var git = new SeqFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        // Act
        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Assert
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    // ==========================================================================
    // Test 2: ancestor-skip fires on upstream failure; dispatcher exit-code mapping
    // ==========================================================================

    /// <summary>
    /// When a ticket in level-0 fails, ParallelDispatcher must stop before running
    /// level-1 (the dependent ticket), set Success=false, and populate FailureReason.
    /// This pins the ancestor-skip / stop-on-failure behavior at the dispatcher layer.
    /// </summary>
    [Fact]
    public async Task Dispatcher_UpstreamFailure_DependentNotDispatched_SuccessFalse()
    {
        // Arrange: A -> B (B blocked by A). A fails; B must not run.
        var callOrder = new List<string>();
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = new ChainResult(
                TicketId: "A",
                Steps: Array.Empty<ChainStep>(),
                Outcome: ChainOutcome.StoppedAtPlan,
                TotalDuration: TimeSpan.FromMilliseconds(5),
                FinalRationale: "plan failed"),
            ["B"] = new ChainResult(
                TicketId: "B",
                Steps: Array.Empty<ChainStep>(),
                Outcome: ChainOutcome.Completed,
                TotalDuration: TimeSpan.FromMilliseconds(10),
                FinalRationale: null)
        };

        var sink = new SeqFakeEventSink();
        async Task<ChainResult> RunChain(ChainPhaseOptions opts, CancellationToken ct)
        {
            callOrder.Add(opts.TicketId);
            if (!results.TryGetValue(opts.TicketId, out var r))
                return new ChainResult(opts.TicketId, Array.Empty<ChainStep>(), ChainOutcome.Completed, TimeSpan.Zero, null);
            await Task.Yield();
            return r;
        }

        var g = new TicketGraph();
        g.AddNode("A");
        g.AddNode("B");
        g.AddEdge("A", "B"); // A blocks B

        var dispatcher = new ParallelDispatcher(RunChain, sink, maxConcurrency: 4,
            sessionIdGenerator: () => "seq-test-session");

        var baseOpts = new ChainPhaseOptions(TicketId: "ignored", Debug: false);

        // Act
        var outcome = await dispatcher.RunAsync(new[] { "A", "B" }, g, baseOpts, CancellationToken.None);

        // Assert
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureReason);
        Assert.Contains("A", outcome.FailureReason);

        // Only A was dispatched; B was blocked by A's failure.
        Assert.Equal(new[] { "A" }, callOrder);
        // Result set contains only A.
        Assert.Single(outcome.Results);
        Assert.Equal("A", outcome.Results[0].TicketId);
        Assert.Equal(ChainOutcome.StoppedAtPlan, outcome.Results[0].Outcome);
    }

    /// <summary>
    /// A successful single-ticket dispatch must produce Success=true with the correct
    /// outcome, confirming the exit-code mapping is unaffected by the width-1 pinning.
    /// </summary>
    [Fact]
    public async Task Dispatcher_SingleTicketSuccess_ExitCodeMappingUnchanged()
    {
        var g = new TicketGraph();
        g.AddNode("A");

        var sink = new SeqFakeEventSink();
        Task<ChainResult> RunChain(ChainPhaseOptions opts, CancellationToken ct) =>
            Task.FromResult(new ChainResult("A", Array.Empty<ChainStep>(), ChainOutcome.Completed, TimeSpan.Zero, null));

        var dispatcher = new ParallelDispatcher(RunChain, sink, maxConcurrency: 4,
            sessionIdGenerator: () => "seq-exit-test");
        var baseOpts = new ChainPhaseOptions(TicketId: "ignored", Debug: false);

        var outcome = await dispatcher.RunAsync(new[] { "A" }, g, baseOpts, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Null(outcome.FailureReason);
        Assert.Single(outcome.Results);
        Assert.Equal(ChainOutcome.Completed, outcome.Results[0].Outcome);
    }

    /// <summary>
    /// When a parent child in level-0 fails, the parent chain stops early and does not
    /// dispatch children in subsequent levels. This pins the anyStoppedEarly path in
    /// RunParentChainAsync, which is the parent-chain equivalent of ancestor-skip.
    /// </summary>
    [Fact]
    public async Task ParentChain_UpstreamChildFails_DependentChildNotRun()
    {
        // Arrange: child1 (TLB-2) fails; child2 (TLB-3) is blocked_by child1.
        // Level 0 = [TLB-2], level 1 = [TLB-3]. After TLB-2 fails, level 1 must not run.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });
        ticketing.SeedRelations(Child2Id, new[] { new Relation("blocked_by", Child1Id) });

        var verifiers = new Queue<IVerifier>();
        // No verifiers needed: plan fails before review for child1;
        // child2 is never reached.

        var git = new SeqFakeGitClient();
        // planWorkerFails=true makes the plan phase fail for every ticket (child1 fails at plan).
        var chain = BuildChain(ticketing, verifiers, git, planWorkerFails: true);

        // Act
        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Assert: parent stopped early because child1 failed.
        Assert.Equal(ChainOutcome.ParentStoppedEarly, result.Outcome);
        Assert.NotNull(result.ChildResults);

        // Only child1 ran; child2 (level 1) was never dispatched because level 0 failed.
        Assert.Single(result.ChildResults!);
        Assert.Equal(Child1Id, result.ChildResults[0].TicketId);
        Assert.NotEqual(ChainOutcome.Completed, result.ChildResults[0].Outcome);
    }

    // ==========================================================================
    // Test 3: multi-ticket parent chain creates and removes exactly one worktree
    // ==========================================================================

    [Fact]
    public async Task ParentChain_TwoChildren_CreatesAndRemovesExactlyOneWorktree()
    {
        // Arrange: two independent children; no blocked_by edges -> both in level 0.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new SeqPassVerifier());
        verifiers.Enqueue(new SeqPassVerifier());

        // Use a tracking git fake that counts worktree create/remove calls.
        var git = new SeqTrackingGitClient();
        _sessionCounter = 0;
        var events = new SeqFakeEventSink();
        var baseOpts = MakeBaseOptions();

        var planWorker = new SeqOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());
        var implWorker = new SeqOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, events, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, ReviewPhase> reviewFactory = opts =>
        {
            var verifier = verifiers.Dequeue();
            return new ReviewPhase(ticketing, new SeqOkWorkerAgent(null, null), events, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new SeqFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new SeqFakeDecrufter(git),
                processPathProvider: () => null);

        Func<BuildOptions, ShipPhase> chainShipFactory = opts =>
            new ShipPhase(ticketing, events, opts,
                MakeShipOptions() with { SkipDecruft = true }, git,
                checksRunner: new SeqFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new SeqFakeDecrufter(git),
                processPathProvider: () => null);

        var chain = new ChainPhase(
            ticketing, events, baseOpts,
            planFactory, implFactory, reviewFactory, shipFactory,
            sessionIdGenerator: NextSessionId,
            workingDirectory: WorkDir,
            ratifierFactory: null,
            chainShipFactory: chainShipFactory,
            gitClient: git);

        // Act
        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Assert: chain completed and created one integration worktree plus one fresh
        // worktree per leaf. Integration branches/worktrees are retained for resume.
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);

        // Total creates: 1 parent integration worktree + 2 child leaf worktrees.
        Assert.Equal(3, git.CreateWorktreeCallCount);

        // Chain-level cleanup no longer deletes the accumulated integration topology.
        Assert.Equal(0, git.RemoveWorktreeCallCount);
    }

    // ==========================================================================
    // Test 4: the shared chain/<slug> placeholder branch is cleaned up and self-healed
    // ==========================================================================

    [Fact]
    public async Task ParentChain_RetainsIntegrationBranch_AtChainEnd()
    {
        // The parent owns a chain/<slug> integration branch. It is the accumulated subtree branch
        // and must remain after chain completion so failed/retried chains can resume from it.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1 });

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new SeqPassVerifier());

        var git = new SeqFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        var chainBranch = "chain/" + PhaseWorktreeLayout.Compute(ParentId, ParentTitle, Path.GetTempPath()).Slug;
        Assert.DoesNotContain(chainBranch, git.DeletedBranches);
    }

    [Fact]
    public async Task ParentChain_LeftoverChainBranch_ChecksOutExistingIntegrationBranch()
    {
        // A prior interrupted chain left a chain/<slug> integration branch behind. The chain must
        // check it out and continue accumulating, not delete the branch and lose prior subtree work.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1 });

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new SeqPassVerifier());

        var git = new SeqFakeGitClient(leftoverChainBranch: true);
        var chain = BuildChain(ticketing, verifiers, git);

        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
        Assert.True(git.CheckoutWorktreeCallCount >= 1, "existing integration branch should be checked out");
        var chainBranch = "chain/" + PhaseWorktreeLayout.Compute(ParentId, ParentTitle, Path.GetTempPath()).Slug;
        Assert.DoesNotContain(chainBranch, git.DeletedBranches);
    }

    [Fact]
    public async Task ParentChain_RetainsShippedChildBranches_AtChainEnd()
    {
        // Each child now runs in a fresh leaf worktree cut from the current integration branch.
        // The chain does not delete those branches during chain-level cleanup; completed branch
        // topology remains available for resume/debugging.
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new SeqFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new SeqPassVerifier());
        verifiers.Enqueue(new SeqPassVerifier());

        var git = new SeqFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.All(result.ChildResults!, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));

        Assert.DoesNotContain(PhaseWorktreeLayout.BranchName(Child1Id), git.DeletedBranches);
        Assert.DoesNotContain(PhaseWorktreeLayout.BranchName(Child2Id), git.DeletedBranches);
    }

    // ==========================================================================
    // Fakes
    // ==========================================================================

    private sealed class SeqFakeTicketing : ITicketing
    {
        private Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        private readonly Dictionary<string, Ticket> _extraTickets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Ticket>> _childrenByParentUuid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Relation>> _relationsByTicketId = new(StringComparer.Ordinal);

        public SeqFakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedChildren(string parentUuid, IReadOnlyList<Ticket> children)
        {
            _childrenByParentUuid[parentUuid] = children.ToList();
            foreach (var c in children)
                _extraTickets[c.Id] = c;
        }

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
            if (_extraTickets.TryGetValue(id, out var extra))
            {
                _extraTickets[id] = extra with { State = newState };
                if (newState == TicketState.InReview)
                    _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(),
                        $"<p>[implemented_at: {CommitSha}]</p>", DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            }
            _ticket = _ticket with { State = newState };
            if (newState == TicketState.InReview)
                _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(),
                    $"<p>[implemented_at: {CommitSha}]</p>", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));
            return Task.FromResult("comment-id");
        }

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
        {
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

    private sealed class SeqOkWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;

        public SeqOkWorkerAgent(IReadOnlyDictionary<string, object>? metadata,
            IReadOnlyDictionary<string, string>? blocks)
        {
            _metadata = metadata;
            _blocks = blocks;
        }

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(new WorkerResult(
                Status.Ok, "ok", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>(), _blocks));
    }

    private sealed class SeqFailWorkerAgent : IWorkerAgent
    {
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(new WorkerResult(
                Status.Failed, "failed", Array.Empty<string>(), "worker error",
                new Dictionary<string, object>()));
    }

    private sealed class SeqPassVerifier : IVerifier
    {
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>()));
    }

    private sealed class SeqFakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SeqFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public SeqFakeChecksRunner(IReadOnlyList<CheckResult> results) { _results = results; }
        public new Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class SeqFakeDecrufter : WorktreeDecrufter
    {
        public SeqFakeDecrufter(IGitClient git) : base(git) { }
        public new Task<DecruftResult> DecruftAsync(string featureWorktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }

    /// <summary>
    /// Basic git fake: worktrees are tracked in a list so ListWorktreesAsync reflects
    /// created entries. CreateWorktreeAsync and RemoveWorktreeAsync counts are exposed
    /// for the single-worktree assertion.
    /// </summary>
    private sealed class SeqFakeGitClient : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = new();
        private readonly HashSet<string> _existingBranches = new(StringComparer.Ordinal);
        private readonly bool _leftoverChainBranch;

        public SeqFakeGitClient(bool leftoverChainBranch = false)
        {
            _leftoverChainBranch = leftoverChainBranch;
            // Seed the main worktree entry so ShipPhase can locate the feature worktree.
            _worktrees.Add(new WorktreeInfo("/fake/main", "main", MainSha, true, false));
        }

        public int CreateWorktreeCallCount { get; private set; }
        public int RemoveWorktreeCallCount { get; private set; }
        public int CreateBranchCallCount { get; private set; }
        public int CheckoutWorktreeCallCount { get; private set; }
        public List<string> DeletedBranches { get; } = new();

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        {
            RemoveWorktreeCallCount++;
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            CreateWorktreeCallCount++;
            // Simulate a leftover chain/<slug> placeholder branch from a prior interrupted run: the
            // first attempt to create the shared worktree collides, exactly like the real git error.
            // Once the self-heal deletes the branch, the retry succeeds.
            if (_leftoverChainBranch && newBranch.StartsWith("chain/", StringComparison.Ordinal)
                && !DeletedBranches.Contains(newBranch))
            {
                _existingBranches.Add(newBranch);
                return Task.FromResult(new WorktreeCreateResult(false,
                    $"fatal: a branch named '{newBranch}' already exists", null));
            }
            Directory.CreateDirectory(worktreePath);
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, CommitSha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<IReadOnlyList<string>> ListLocalBranchesAsync(string pattern, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _existingBranches.Contains(pattern) ||
                (_leftoverChainBranch && pattern.StartsWith("chain/", StringComparison.Ordinal))
                    ? new[] { pattern }
                    : Array.Empty<string>());

        public Task<WorktreeCreateResult> CheckoutWorktreeAsync(string worktreePath, string existingBranch, string mainWorktreePath, CancellationToken ct)
        {
            CheckoutWorktreeCallCount++;
            Directory.CreateDirectory(worktreePath);
            _worktrees.Add(new WorktreeInfo(worktreePath, existingBranch, CommitSha, false, false));
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

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            DeletedBranches.Add(branch);
            _existingBranches.Remove(branch);
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);

        // Required for shared-worktree path: ImplementPhase calls this instead of CreateWorktreeAsync
        // when SharedWorktreePath is set. Update the tracked branch name on the shared worktree entry.
        public Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct)
        {
            CreateBranchCallCount++;
            // Update the branch on the existing shared-worktree entry so ShipPhase can find it.
            for (int i = 0; i < _worktrees.Count; i++)
            {
                string wPathFull;
                try { wPathFull = Path.GetFullPath(_worktrees[i].Path); }
                catch { wPathFull = _worktrees[i].Path; }
                string targetFull;
                try { targetFull = Path.GetFullPath(worktreePath); }
                catch { targetFull = worktreePath; }
                if (string.Equals(wPathFull, targetFull, StringComparison.OrdinalIgnoreCase))
                {
                    _worktrees[i] = _worktrees[i] with { Branch = branch };
                    break;
                }
            }
            return Task.FromResult(new GitOpResult(true, null));
        }
    }

    /// <summary>
    /// A tracking git fake identical to SeqFakeGitClient but with worktree counts
    /// exposed explicitly. Alias used by the single-worktree test to make intent clear.
    /// </summary>
    private sealed class SeqTrackingGitClient : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = new();

        public SeqTrackingGitClient()
        {
            _worktrees.Add(new WorktreeInfo("/fake/main", "main", MainSha, true, false));
        }

        public int CreateWorktreeCallCount { get; private set; }
        public int RemoveWorktreeCallCount { get; private set; }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        {
            RemoveWorktreeCallCount++;
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            CreateWorktreeCallCount++;
            Directory.CreateDirectory(worktreePath);
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, CommitSha, false, false));
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

        // Required for shared-worktree path: ImplementPhase calls this when SharedWorktreePath is set.
        public Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct)
        {
            for (int i = 0; i < _worktrees.Count; i++)
            {
                string wPathFull;
                try { wPathFull = Path.GetFullPath(_worktrees[i].Path); }
                catch { wPathFull = _worktrees[i].Path; }
                string targetFull;
                try { targetFull = Path.GetFullPath(worktreePath); }
                catch { targetFull = worktreePath; }
                if (string.Equals(wPathFull, targetFull, StringComparison.OrdinalIgnoreCase))
                {
                    _worktrees[i] = _worktrees[i] with { Branch = branch };
                    break;
                }
            }
            return Task.FromResult(new GitOpResult(true, null));
        }
    }
}
