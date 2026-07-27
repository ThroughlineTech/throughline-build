using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the chain half of the sequence-contract (Brief 18, Plan E):
///   The chain reads back blocked_by relations from Plane and dispatches tickets in
///   the dependency-correct order so a dependent ticket always runs after its dependency.
///
/// Tests exercise the relationship between Plane's blocked_by data and the chain's
/// actual dispatch order, keeping the two halves (scaffold encodes, chain reads) together.
/// All tests are hermetic: no real repository or Plane backend.
/// </summary>
public class SequenceContractChainTests
{
    // ----- constants -----

    private const string ParentId = "TLB-10";
    private const string ParentUuid = "parent-uuid-10";
    private const string ParentTitle = "Sequence contract parent";

    private const string Child1Id = "TLB-11";
    private const string Child1Uuid = "child-uuid-11";

    private const string Child2Id = "TLB-12";
    private const string Child2Uuid = "child-uuid-12";

    private const string Child3Id = "TLB-13";
    private const string Child3Uuid = "child-uuid-13";

    private const string MainSha = "aabbccddeeff00112233445566778899aabbccdd";
    private const string CommitSha = "0011223344556677889900aabbccddeeff001122";

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
        SessionId: "seq-contract-session",
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

    private int _sessionCounter;
    private string NextSessionId() => $"sc-session-{++_sessionCounter}";

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
        Title: $"Child {id}",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>child</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: ParentUuid);

    /// <summary>
    /// Builds a ChainPhase with the supplied fakes.
    /// </summary>
    private ChainPhase BuildChain(
        ScFakeTicketing ticketing,
        Queue<IVerifier> verifierQueue,
        ScFakeGitClient git)
    {
        _sessionCounter = 0;
        var events = new ScFakeEventSink();
        var baseOpts = MakeBaseOptions();

        var planWorker = new ScOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());
        var implWorker = new ScOkWorkerAgent(OkWorkerMeta(), OkWorkerBlocks());

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, implWorker, events, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory = (opts, _) =>
        {
            var verifier = verifierQueue.Dequeue();
            return new ReviewPhase(ticketing, new ScOkWorkerAgent(null, null), events, opts,
                MakeReviewOptions(), git, verifierOverride: verifier);
        };

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new ScFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new ScFakeDecrufter(git),
                processPathProvider: () => null);

        Func<BuildOptions, ShipPhase> chainShipFactory = opts =>
            new ShipPhase(ticketing, events, opts,
                MakeShipOptions() with { SkipDecruft = true }, git,
                checksRunner: new ScFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new ScFakeDecrufter(git),
                processPathProvider: () => null);

        return new ChainPhase(
            new ChainPhaseCoreDependencies
            {
                Ticketing = ticketing,
                Events = events,
                BaseOptions = baseOpts,
                Git = git,
                SessionIdGenerator = NextSessionId,
                WorkingDirectory = WorkDir
            },
            new ChainPhaseFactories
            {
                Plan = planFactory,
                Implement = implFactory,
                Review = reviewFactory,
                Ship = shipFactory,
                ChainShip = chainShipFactory,
                Gate = null,
                Ratifier = null
            },
            new ChainPhaseExecutionDependencies
            {
                FeedbackRetriever = null,
                BatchWorker = null,
                LandingRemote = null,
                LandingPushEnabled = false,
                ReworkRecheckSpecs = null,
                ReworkRecheckRunner = null,
                Output = null
            });
    }

    // ==========================================================================
    // Test 1: chain reads blocked_by relations and dispatches in correct order
    // ==========================================================================

    /// <summary>
    /// Given three children where:
    ///   TLB-12 blocked_by TLB-11  (level 0: TLB-11; level 1: TLB-12)
    ///   TLB-13 blocked_by TLB-12  (level 2: TLB-13)
    /// the chain must dispatch them strictly in order: TLB-11, TLB-12, TLB-13.
    ///
    /// This closes the sequence-contract read-back loop: scaffold encodes the deps,
    /// Plane stores them as blocked_by, and the chain reads them and orders by them.
    /// </summary>
    [Fact]
    public async Task Chain_ThreeChainedChildren_BlockedByRelations_DispatchesInDependencyOrder()
    {
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);
        var child3 = MakeChild(Child3Id, Child3Uuid, TicketState.Backlog);

        var ticketing = new ScFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2, child3 });

        // Simulate Plane returning blocked_by relations that scaffold would have created.
        // TLB-12 blocked_by TLB-11 (child2 must run after child1)
        // TLB-13 blocked_by TLB-12 (child3 must run after child2)
        ticketing.SeedRelations(Child2Id, new[] { new Relation("blocked_by", Child1Id) });
        ticketing.SeedRelations(Child3Id, new[] { new Relation("blocked_by", Child2Id) });

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new ScPassVerifier()); // child1 review
        verifiers.Enqueue(new ScPassVerifier()); // child2 review
        verifiers.Enqueue(new ScPassVerifier()); // child3 review

        var git = new ScFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        // Act
        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Assert: all three complete
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(3, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));

        // Dependency order: TLB-11 (level 0) -> TLB-12 (level 1) -> TLB-13 (level 2).
        Assert.Equal(Child1Id, result.ChildResults[0].TicketId);
        Assert.Equal(Child2Id, result.ChildResults[1].TicketId);
        Assert.Equal(Child3Id, result.ChildResults[2].TicketId);
    }

    /// <summary>
    /// When two independent children (no blocked_by edge between them) are present,
    /// both run successfully and the result contains both.
    /// A missing edge between them is visible by the fact they appear in the same level.
    /// </summary>
    [Fact]
    public async Task Chain_TwoIndependentChildren_NoBlockedByEdge_BothComplete()
    {
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        var ticketing = new ScFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });
        // No relations seeded -> both are in level 0 (unordered relative to each other).

        var verifiers = new Queue<IVerifier>();
        verifiers.Enqueue(new ScPassVerifier());
        verifiers.Enqueue(new ScPassVerifier());

        var git = new ScFakeGitClient();
        var chain = BuildChain(ticketing, verifiers, git);

        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);
        Assert.NotNull(result.ChildResults);
        Assert.Equal(2, result.ChildResults!.Count);
        Assert.All(result.ChildResults, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    // ==========================================================================
    // Fakes
    // ==========================================================================

    private sealed class ScFakeTicketing : ITicketing
    {
        private Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        private readonly Dictionary<string, Ticket> _extraTickets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Ticket>> _childrenByParentUuid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Relation>> _relationsByTicketId = new(StringComparer.Ordinal);

        public ScFakeTicketing(Ticket ticket) { _ticket = ticket; }

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

    private sealed class ScOkWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;

        public ScOkWorkerAgent(IReadOnlyDictionary<string, object>? metadata,
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

    private sealed class ScPassVerifier : IVerifier
    {
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>()));
    }

    private sealed class ScFakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ScFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public ScFakeChecksRunner(IReadOnlyList<CheckResult> results) { _results = results; }
        public new Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class ScFakeDecrufter : WorktreeDecrufter
    {
        public ScFakeDecrufter(IGitClient git) : base(git) { }
        public new Task<DecruftResult> DecruftAsync(string featureWorktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }

    private sealed class ScFakeGitClient : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = new();

        public ScFakeGitClient()
        {
            _worktrees.Add(new WorktreeInfo("/fake/main", "main", MainSha, true, false));
        }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.AsReadOnly());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        {
            _worktrees.RemoveAll(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
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
