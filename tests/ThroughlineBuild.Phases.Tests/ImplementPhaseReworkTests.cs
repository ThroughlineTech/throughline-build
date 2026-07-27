using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ImplementPhaseReworkTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket(TicketState state) => new Ticket(
        Id: "TLB-1",
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-rework",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs" }
        });

    private static ReviewFeedback MakeReviewFeedback(int round = 1) => new ReviewFeedback(
        Rationale: "Tests were not written.",
        ChecksFailed: new[] { "tests_pass", "coverage_ok" },
        ReworkRoundNumber: round);

    // Creates a temp directory and pre-creates the worktree subdirectory inside it,
    // simulating a ticket whose initial implement already ran.
    private static string CreateTempWorkingDirWithWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var names = PhaseWorktreeLayout.Compute("TLB-1", "Test ticket", root);
        Directory.CreateDirectory(names.WorktreePath);
        return root;
    }

    [Fact]
    public async Task RunAsync_ReworkRound_InProgressWithFeedback_Succeeds()
    {
        var workingDir = CreateTempWorkingDirWithWorktree();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", workingDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, result.ReworkRoundNumber);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_SkipsStartTransitionAndEndsInReview()
    {
        var workingDir = CreateTempWorkingDirWithWorktree();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        await phase.RunAsync("TLB-1", workingDir, CancellationToken.None);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_InitialRound_ReadyWithNoFeedback_Succeeds()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ReworkRoundNumber);
        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[1].state);
    }

    [Fact]
    public async Task RunAsync_InitialRound_AgainstInProgress_FailsClearly()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("build rework", result.FailureReason ?? "");
        Assert.Contains("InProgress", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_InitialRound_AgainstInReview_PointsToReviewNotRework()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("InReview", result.FailureReason ?? "");
        Assert.Contains("build review", result.FailureReason ?? "");
        // The blanket "did you mean to invoke rework?" hint is wrong from InReview
        // (rework requires InProgress), so it must not be surfaced here.
        Assert.DoesNotContain("did you mean to invoke rework", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_InitialRound_AgainstDone_FailsClearly()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Done));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Done", result.FailureReason ?? "");
        Assert.Contains("nothing to implement", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_AgainstReady_FailsClearly()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no review has run yet", result.FailureReason ?? "");
        Assert.Contains("Ready", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_ReworkRoundNumberPropagatesFromFeedbackToResult()
    {
        var workingDir = CreateTempWorkingDirWithWorktree();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(2));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", workingDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ReworkRoundNumber);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_BriefIncludesPriorSummaryAndTouchedFiles()
    {
        var workingDir = CreateTempWorkingDirWithWorktree();
        var implementedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        ticketing.ExistingComments.Add(new TicketComment(
            "comment-implemented",
            $"<p class=\"editor-paragraph-block\" data-id=\"marker\">[implemented_at: {CommitSha}] (branch ticket/tlb-1-test-ticket)</p><p>Implemented parser changes.</p><ul><li>Added regression tests.</li></ul>",
            implementedAt));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha)
        {
            DiffEntries = new[]
            {
                new DiffEntry("src/Parser.cs", DiffKind.Modified, null, 3, 1, "@@ patch should not be included"),
                new DiffEntry("tests/ParserTests.cs", DiffKind.Added, null, 12, 0, "@@ patch should not be included")
            }
        };
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", workingDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(worker.LastBrief);
        Assert.Contains("## Prior implement context", worker.LastBrief!.Instruction);
        Assert.Contains("Implemented parser changes.", worker.LastBrief.Instruction);
        Assert.Contains("Added regression tests.", worker.LastBrief.Instruction);
        Assert.DoesNotContain("implemented_at", worker.LastBrief.Instruction);
        Assert.Contains("- src/Parser.cs", worker.LastBrief.Instruction);
        Assert.Contains("- tests/ParserTests.cs", worker.LastBrief.Instruction);
        Assert.DoesNotContain("@@ patch should not be included", worker.LastBrief.Instruction);
        Assert.Equal(new[] { "src/Parser.cs", "tests/ParserTests.cs" }, worker.LastBrief.RelevantFiles);
        Assert.True(git.DiffCalled);
        Assert.False(git.LastDiffIncludedPatchContent);
    }

    [Fact]
    public async Task RunAsync_InitialRound_BriefDoesNotIncludePriorImplementContext()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        ticketing.ExistingComments.Add(new TicketComment(
            "comment-implemented",
            $"<p>[implemented_at: {CommitSha}]</p><p>Should stay off initial brief.</p>",
            DateTimeOffset.UtcNow));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha)
        {
            DiffEntries = new[] { new DiffEntry("src/Initial.cs", DiffKind.Modified, null, 1, 1, null) }
        };
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(worker.LastBrief);
        Assert.DoesNotContain("Prior implement context", worker.LastBrief!.Instruction);
        Assert.DoesNotContain("Should stay off initial brief.", worker.LastBrief.Instruction);
        Assert.Empty(worker.LastBrief.RelevantFiles);
        Assert.False(git.DiffCalled);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_NeverCallsCreateWorktree()
    {
        var workingDir = CreateTempWorkingDirWithWorktree();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        await phase.RunAsync("TLB-1", workingDir, CancellationToken.None);

        Assert.False(git.CreateWorktreeCalled);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_MissingWorktree_FailsClearly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", tempRoot, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.FailureReason ?? "");
        // Recovery was attempted (CheckoutWorktreeAsync) but the branch was unrecoverable.
        Assert.True(git.CheckoutWorktreeCalled);
        Assert.False(git.CreateWorktreeCalled);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ReworkRound_MissingWorktree_RecreatesFromSurvivingBranch_Succeeds()
    {
        // Models resuming a stopped chain child (TLB-438): the shared worktree was torn down
        // at chain end, but the ticket branch survives. The rework round must recreate the
        // worktree from that branch and proceed instead of dead-ending.
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InProgress));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha) { CheckoutWorktreeSucceeds = true };
        var phaseOptions = new ImplementPhaseOptions(MakeReviewFeedback(1));
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git, phaseOptions: phaseOptions);

        var result = await phase.RunAsync("TLB-1", tempRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(git.CheckoutWorktreeCalled);
        Assert.False(git.CreateWorktreeCalled);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Equal(1, result.ReworkRoundNumber);
        Assert.Contains(ticketing.Transitions, t => t.state == TicketState.InReview);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();
        public List<TicketComment> ExistingComments { get; } = new();

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
            Task.FromResult((IReadOnlyList<TicketComment>)ExistingComments);
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

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Brief? LastBrief { get; private set; }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastBrief = brief;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _mainSha;
        private readonly string _headSha;
        public bool CreateWorktreeCalled { get; private set; }
        public bool CheckoutWorktreeCalled { get; private set; }
        public bool DiffCalled { get; private set; }
        public bool LastDiffIncludedPatchContent { get; private set; }
        public IReadOnlyList<DiffEntry> DiffEntries { get; set; } = Array.Empty<DiffEntry>();
        // Default false: a fake with no existing worktree and no recoverable branch.
        // Set true to simulate the surviving ticket branch being re-checkout-able.
        public bool CheckoutWorktreeSucceeds { get; set; }

        public FakeGitClient(string mainSha, string headSha)
        {
            _mainSha = mainSha;
            _headSha = headSha;
        }

        public Task<WorktreeCreateResult> CheckoutWorktreeAsync(string worktreePath, string existingBranch, string mainWorktreePath, CancellationToken ct)
        {
            CheckoutWorktreeCalled = true;
            if (!CheckoutWorktreeSucceeds)
                return Task.FromResult(new WorktreeCreateResult(false, "branch not found", null));
            Directory.CreateDirectory(worktreePath);
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            CreateWorktreeCalled = true;
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(_headSha);
        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct)
        {
            DiffCalled = true;
            LastDiffIncludedPatchContent = includePatchContent;
            return Task.FromResult(new GitDiff(fromRef, toRef, DiffEntries));
        }
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
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
    }
}
