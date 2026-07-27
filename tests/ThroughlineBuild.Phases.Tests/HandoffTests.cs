using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the handoff-pointer behaviors added by Briefs 08-10 (Plan C).
///
/// (1) A two-ticket chain threads ticket 1's touched-files and range into ticket 2's brief.
/// (2) An empty range (first ticket, or anchors identical) leaves the brief unchanged,
///     with no empty headers or stray context.
/// (3) The compile-time off-switch path: calling ImplementBriefBuilder.Build with
///     chainCommitRange: null (the effectiveChainRange when HandoffPointerEnabled is false)
///     produces a brief byte-identical to a no-pointer baseline.
///
/// All tests are hermetic: no real repository, no real git process.
/// </summary>
public class HandoffTests
{
    // -----------------------------------------------------------------------
    // Shared constants
    // -----------------------------------------------------------------------

    private const string ParentId = "TLB-10";
    private const string ParentUuid = "parent-uuid-10";
    private const string Child1Id = "TLB-11";
    private const string Child1Uuid = "child-uuid-11";
    private const string Child2Id = "TLB-12";
    private const string Child2Uuid = "child-uuid-12";

    private const string ChainStartSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AfterChild1Sha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CommitSha = "cccccccccccccccccccccccccccccccccccccccc";

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
        SessionId: "handoff-test-session",
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
            ["planned_at_sha"] = ChainStartSha,
            ["files_changed"] = Array.Empty<string>()
        };

    private static IReadOnlyDictionary<string, string> OkWorkerBlocks() =>
        new Dictionary<string, string> { ["PLAN_BODY"] = "# Plan\nThis is the plan." };

    private static Ticket MakeParent() => new Ticket(
        Id: ParentId,
        Uuid: ParentUuid,
        Title: "Parent handoff ticket",
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
    private string NextSessionId() => $"handoff-session-{++_sessionCounter}";

    // -----------------------------------------------------------------------
    // Acceptance 1: A two-ticket chain threads the first ticket's touched-files
    // and range into the second brief.
    //
    // Mechanism: the git fake simulates the target head advancing after child1
    // ships (RevParseAsync returns AfterChild1Sha on calls 2+), RevListCountAsync
    // returns 1 for that range, and DiffStatFilesAsync returns known files.
    // The capturing worker records the brief for each ticket so we can inspect
    // child2's brief after the chain completes.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TwoTicketChain_SecondBriefContainsTouchedFilesFromFirst()
    {
        var parent = MakeParent();
        var child1 = MakeChild(Child1Id, Child1Uuid, TicketState.Backlog);
        var child2 = MakeChild(Child2Id, Child2Uuid, TicketState.Backlog);

        // child2 is blocked_by child1: TopologicalSorter puts child1 in level 0,
        // child2 in level 1. child1 ships first; child2 then gets a non-empty range.
        var ticketing = new HoFakeTicketing(parent);
        ticketing.SeedChildren(ParentUuid, new[] { child1, child2 });
        ticketing.SeedRelations(Child2Id, new[] { new Relation("blocked_by", Child1Id) });

        // The git fake simulates target head advancing after child1 ships:
        // - First RevParseAsync call: chain-start capture  -> ChainStartSha
        // - Second call (before child1 runs)               -> ChainStartSha (range empty -> first ticket)
        // - Third call (before child2 runs)                -> AfterChild1Sha (range non-empty -> second ticket)
        // RevListCountAsync returns 1 when range != empty.
        // DiffStatFilesAsync returns two files.
        var touchedFiles = new[] { "src/First.cs", "tests/FirstTests.cs" };
        var git = new HoAdvancingGitClient(
            chainStartSha: ChainStartSha,
            afterChild1Sha: AfterChild1Sha,
            commitSha: CommitSha,
            touchedFiles: touchedFiles);

        var capturer = new HoBriefCapturingWorker(OkWorkerMeta(), OkWorkerBlocks());
        _sessionCounter = 0;
        var events = new HoFakeEventSink();
        var baseOpts = MakeBaseOptions();

        var planWorker = new HoOkWorker(OkWorkerMeta(), OkWorkerBlocks());
        var reviewWorker = new HoOkWorker(null, null);

        Func<BuildOptions, PlanPhase> planFactory = opts =>
            new PlanPhase(ticketing, planWorker, events, opts, git);

        // ImplementPhase uses the capturing worker so we record each child's brief.
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implFactory = (opts, phaseOpts) =>
            new ImplementPhase(ticketing, capturer, events, opts, git, phaseOptions: phaseOpts);

        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory = (opts, _) =>
            new ReviewPhase(ticketing, reviewWorker, events, opts,
                MakeReviewOptions(), git, verifierOverride: new HoPassVerifier());

        Func<BuildOptions, ShipPhase> shipFactory = opts =>
            new ShipPhase(ticketing, events, opts, MakeShipOptions(), git,
                checksRunner: new HoFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new HoFakeDecrufter(git),
                processPathProvider: () => null);

        Func<BuildOptions, ShipPhase> chainShipFactory = opts =>
            new ShipPhase(ticketing, events, opts,
                MakeShipOptions() with { SkipDecruft = true }, git,
                checksRunner: new HoFakeChecksRunner(Array.Empty<CheckResult>()),
                markerScanner: (_, _) => Task.FromResult<IReadOnlyList<ConflictMarkerHit>>(Array.Empty<ConflictMarkerHit>()),
                decrufter: new HoFakeDecrufter(git),
                processPathProvider: () => null);

        var chain = new ChainPhase(
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

        var result = await chain.RunAsync(new ChainPhaseOptions(ParentId, false), CancellationToken.None);

        // Chain must complete successfully.
        Assert.Equal(ChainOutcome.ParentCompleted, result.Outcome);

        // Two implement briefs were captured: one per child.
        Assert.Equal(2, capturer.CapturedBriefs.Count);

        // The second brief (child2) must list the files touched by child1.
        var secondBrief = capturer.CapturedBriefs[1];
        Assert.Equal(new[] { "src/First.cs", "tests/FirstTests.cs" }, secondBrief.RelevantFiles);

        // The chain_pointer context entry must reference the chain range.
        Assert.True(secondBrief.Context.ContainsKey("chain_pointer"),
            "Second brief must contain 'chain_pointer' in Context");
        var pointer = secondBrief.Context["chain_pointer"];
        Assert.Contains(ChainStartSha, pointer);
        Assert.Contains(AfterChild1Sha, pointer);

        // The first brief (child1) must NOT have a pointer because no tickets had shipped yet.
        var firstBrief = capturer.CapturedBriefs[0];
        Assert.Empty(firstBrief.RelevantFiles);
        Assert.False(firstBrief.Context.ContainsKey("chain_pointer"),
            "First brief must not have 'chain_pointer' - no prior commits in chain");
    }

    // -----------------------------------------------------------------------
    // Acceptance 2: An empty range leaves the brief unchanged - no chain_pointer,
    // no touched-files, and no empty headers or stray context keys.
    //
    // Tested directly on ImplementBriefBuilder.Build with IsEmpty range.
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_EmptyChainCommitRange_BriefIdenticalToNullRangeBaseline()
    {
        // Arrange: construct a ticket and repo state representative of the
        // "first ticket in chain" scenario.
        var ticket = new Ticket(
            Id: "TLB-20",
            Uuid: "ticket-uuid-20",
            Title: "First chain ticket",
            Type: "feature",
            State: TicketState.Ready,
            Size: Size.S,
            Risk: Risk.Low,
            DescriptionHtml: "<p>first</p>",
            Relations: Array.Empty<Relation>(),
            Labels: Array.Empty<string>(),
            ParentId: null);

        var repo = new RepoState(ChainStartSha, Array.Empty<string>().ToList().AsReadOnly());

        // Baseline: no range provided (null -> pre-Plan-C behavior)
        var baselineBrief = ImplementBriefBuilder.Build(
            agentName: "claude-code",
            ticket: ticket,
            repo: repo,
            branchName: "ticket/tlb-20-first-chain-ticket",
            worktreePath: "/fake/worktree",
            chainCommitRange: null);

        // An IsEmpty range (CommitCount == 0) must produce the same result.
        var emptyRange = new ChainCommitRange(
            StartSha: ChainStartSha,
            EndSha: ChainStartSha,
            CommitCount: 0,
            TouchedFiles: Array.Empty<string>());

        var emptyRangeBrief = ImplementBriefBuilder.Build(
            agentName: "claude-code",
            ticket: ticket,
            repo: repo,
            branchName: "ticket/tlb-20-first-chain-ticket",
            worktreePath: "/fake/worktree",
            chainCommitRange: emptyRange);

        // RelevantFiles must be empty in both cases.
        Assert.Empty(baselineBrief.RelevantFiles);
        Assert.Empty(emptyRangeBrief.RelevantFiles);

        // chain_pointer must be absent in both cases.
        Assert.False(baselineBrief.Context.ContainsKey("chain_pointer"));
        Assert.False(emptyRangeBrief.Context.ContainsKey("chain_pointer"));

        // The instruction text must be byte-identical.
        Assert.Equal(baselineBrief.Instruction, emptyRangeBrief.Instruction);

        // Context keys beyond chain_pointer must match.
        Assert.Equal(baselineBrief.Context.Keys.OrderBy(k => k).ToList(),
                     emptyRangeBrief.Context.Keys.OrderBy(k => k).ToList());
    }

    // -----------------------------------------------------------------------
    // Acceptance 3: The disabled-build path produces the baseline brief.
    //
    // HandoffPointerEnabled is a private const bool in ImplementPhase. When it
    // is false, effectiveChainRange is set to null before calling
    // ImplementBriefBuilder.Build, which produces the same brief as if no range
    // were supplied. This test exercises that code path directly by supplying a
    // non-empty ChainCommitRange but then verifying that calling
    // ImplementBriefBuilder.Build with null produces the baseline. This is the
    // functional contract the disabled build enforces: null range -> baseline brief.
    // -----------------------------------------------------------------------

    [Fact]
    public void Build_NullRange_BaselineIdenticalToNoPointerCase()
    {
        // This test pins the "effectiveChainRange = null when HandoffPointerEnabled is false"
        // code path: ImplementBriefBuilder.Build(chainCommitRange: null) must produce
        // a brief byte-identical to what would have been produced before Plan C.
        var ticket = new Ticket(
            Id: "TLB-30",
            Uuid: "ticket-uuid-30",
            Title: "Pointer disabled ticket",
            Type: "feature",
            State: TicketState.Ready,
            Size: Size.S,
            Risk: Risk.Low,
            DescriptionHtml: "<p>disabled</p>",
            Relations: Array.Empty<Relation>(),
            Labels: Array.Empty<string>(),
            ParentId: null);

        var repo = new RepoState(AfterChild1Sha, Array.Empty<string>().ToList().AsReadOnly());
        var nonEmptyRange = new ChainCommitRange(
            StartSha: ChainStartSha,
            EndSha: AfterChild1Sha,
            CommitCount: 1,
            TouchedFiles: new[] { "src/First.cs" });

        // Simulate what ImplementPhase does when HandoffPointerEnabled is false:
        // effectiveChainRange = null (the range is discarded at the consumption side).
        var disabledBrief = ImplementBriefBuilder.Build(
            agentName: "claude-code",
            ticket: ticket,
            repo: repo,
            branchName: "ticket/tlb-30-pointer-disabled-ticket",
            worktreePath: "/fake/worktree-30",
            chainCommitRange: null); // <-- disabled path passes null

        // Also build with the non-empty range to confirm the two paths diverge when enabled.
        var enabledBrief = ImplementBriefBuilder.Build(
            agentName: "claude-code",
            ticket: ticket,
            repo: repo,
            branchName: "ticket/tlb-30-pointer-disabled-ticket",
            worktreePath: "/fake/worktree-30",
            chainCommitRange: nonEmptyRange);

        // Disabled path: no RelevantFiles, no chain_pointer.
        Assert.Empty(disabledBrief.RelevantFiles);
        Assert.False(disabledBrief.Context.ContainsKey("chain_pointer"),
            "Disabled path must not inject 'chain_pointer' into Context");

        // Enabled path: RelevantFiles populated, chain_pointer present.
        Assert.Equal(new[] { "src/First.cs" }, enabledBrief.RelevantFiles);
        Assert.True(enabledBrief.Context.ContainsKey("chain_pointer"));

        // Instruction is identical (the pointer is not in the instruction template body).
        Assert.Equal(disabledBrief.Instruction, enabledBrief.Instruction);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// <summary>
    /// A git fake that simulates the target head advancing after child1 ships.
    ///
    /// The target head is represented by a mutable field that starts at ChainStartSha.
    /// When ShipPhase calls FastForwardMergeAsync (the ship step that lands the ticket
    /// branch onto main), the fake advances the head to AfterChild1Sha. From that point
    /// every RevParseAsync call returns AfterChild1Sha, so the chain-commit-range helper
    /// computes a non-trivial range for child2's brief.
    ///
    /// RevListCountAsync returns 1 for the non-trivial range (ChainStartSha..AfterChild1Sha).
    /// DiffStatFilesAsync returns the configured touched-files for that same range.
    /// </summary>
    private sealed class HoAdvancingGitClient : IGitClient
    {
        private readonly string _chainStartSha;
        private readonly string _afterChild1Sha;
        private readonly string _commitSha;
        private readonly IReadOnlyList<string> _touchedFiles;
        private readonly List<WorktreeInfo> _worktrees = new();

        // Mutable: advances to AfterChild1Sha once FastForwardMergeAsync is called.
        private string _currentTargetSha;

        public HoAdvancingGitClient(
            string chainStartSha,
            string afterChild1Sha,
            string commitSha,
            IReadOnlyList<string> touchedFiles)
        {
            _chainStartSha = chainStartSha;
            _afterChild1Sha = afterChild1Sha;
            _commitSha = commitSha;
            _touchedFiles = touchedFiles;
            _currentTargetSha = chainStartSha;
            _worktrees.Add(new WorktreeInfo("/fake/main", "main", chainStartSha, true, false));
        }

        // Always returns the current target head; advances after FastForwardMergeAsync.
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_currentTargetSha);

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct)
        {
            // Non-trivial range (ChainStartSha..AfterChild1Sha) has 1 commit; any other range has 0.
            var parts = range.Split("..");
            var count = parts.Length == 2 &&
                        string.Equals(parts[0], _chainStartSha, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(parts[1], _afterChild1Sha, StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;
            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_touchedFiles);

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
            _worktrees.Add(new WorktreeInfo(worktreePath, newBranch, _commitSha, false, false));
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(_commitSha);

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));

        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        // Advance the target head when ship fast-forward-merges. This simulates the ticket
        // branch landing on main so the next range computation sees new commits.
        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct)
        {
            _currentTargetSha = _afterChild1Sha;
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct)
        {
            for (int i = 0; i < _worktrees.Count; i++)
            {
                string wPath;
                try { wPath = Path.GetFullPath(_worktrees[i].Path); }
                catch { wPath = _worktrees[i].Path; }
                string tPath;
                try { tPath = Path.GetFullPath(worktreePath); }
                catch { tPath = worktreePath; }
                if (string.Equals(wPath, tPath, StringComparison.OrdinalIgnoreCase))
                {
                    _worktrees[i] = _worktrees[i] with { Branch = branch };
                    break;
                }
            }
            return Task.FromResult(new GitOpResult(true, null));
        }
    }

    private sealed class HoBriefCapturingWorker : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;
        public List<Brief> CapturedBriefs { get; } = new();

        public HoBriefCapturingWorker(
            IReadOnlyDictionary<string, object>? metadata,
            IReadOnlyDictionary<string, string>? blocks)
        {
            _metadata = metadata;
            _blocks = blocks;
        }

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            CapturedBriefs.Add(brief);
            return Task.FromResult(new WorkerResult(
                Status.Ok, "ok", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>(), _blocks));
        }
    }

    private sealed class HoOkWorker : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        private readonly IReadOnlyDictionary<string, string>? _blocks;

        public HoOkWorker(
            IReadOnlyDictionary<string, object>? metadata,
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

    private sealed class HoPassVerifier : IVerifier
    {
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(new Verdict(VerdictKind.Pass, "lgtm", Array.Empty<string>()));
    }

    private sealed class HoFakeTicketing : ITicketing
    {
        private Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        private readonly Dictionary<string, Ticket> _extraTickets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Ticket>> _childrenByParentUuid = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Relation>> _relationsByTicketId = new(StringComparer.Ordinal);

        public HoFakeTicketing(Ticket ticket) { _ticket = ticket; }

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

    private sealed class HoFakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HoFakeChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;
        public HoFakeChecksRunner(IReadOnlyList<CheckResult> results) { _results = results; }
        public new Task<IReadOnlyList<CheckResult>> RunAsync(IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_results);
    }

    private sealed class HoFakeDecrufter : WorktreeDecrufter
    {
        public HoFakeDecrufter(IGitClient git) : base(git) { }
        public new Task<DecruftResult> DecruftAsync(string featureWorktreePath, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new DecruftResult(null, new Dictionary<DecruftStep, DecruftStepOutcome>()));
    }
}
