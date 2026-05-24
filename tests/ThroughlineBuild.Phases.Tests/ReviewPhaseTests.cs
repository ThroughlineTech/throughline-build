using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ReviewPhaseTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ImplementedSha = "ffffffffffffffffffffffffffffffffffffffff";
    private const string TicketId = "TLB-1";
    private const string TicketTitle = "Test ticket";
    private const string BranchName = "ticket/tlb-1-test-ticket";

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

    private static ReviewOptions MakeReviewOptions() => new ReviewOptions(
        Checks: Array.Empty<CheckSpec>(),
        VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null, null));

    private static string MakeWorkingDir()
    {
        // PhaseWorktreeLayout computes Path.GetFullPath; we just need a deterministic existing dir.
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public async Task RunAsync_PassVerdict_PostsComment_NoTransition()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "looks good", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);
        Assert.Null(result.FailureReason);

        Assert.Empty(ticketing.Transitions);

        Assert.Single(ticketing.Comments);
        Assert.Contains("reviewed:", ticketing.Comments[0].html);
        Assert.Contains("pass", ticketing.Comments[0].html);
        Assert.Contains("looks good", ticketing.Comments[0].html);

        var spawnEvents = events.Events.Where(e => e.Kind == EventKind.WorkerSpawn).ToList();
        Assert.Single(spawnEvents);
        Assert.Equal("verifier", spawnEvents[0].Data["role"].ToString());

        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Single(verdictEvents);
        Assert.Equal("Pass", verdictEvents[0].Data["kind"].ToString());

        var writeEvents = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        Assert.Single(writeEvents);
        Assert.Equal("create_comment", writeEvents[0].Data["action"].ToString());

        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_ReworkVerdictEmptyChecks_PostsCommentWithoutChecksFailed_TransitionsToInProgress()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Rework, "missing tests", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Rework, result.Verdict);

        Assert.Single(ticketing.Comments);
        Assert.Contains("rework", ticketing.Comments[0].html);
        Assert.DoesNotContain("checks_failed:", ticketing.Comments[0].html);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);

        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Single(stateTransitions);
        Assert.Equal("InReview", stateTransitions[0].Data["from"].ToString());
        Assert.Equal("InProgress", stateTransitions[0].Data["to"].ToString());
    }

    [Fact]
    public async Task RunAsync_ReworkVerdictWithChecks_IncludesChecksFailedList()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Rework, "broken",
            new[] { "build failed", "test x" }));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Rework, result.Verdict);

        Assert.Single(ticketing.Comments);
        Assert.Contains("broken", ticketing.Comments[0].html);
        Assert.Contains("checks_failed: build failed, test x", ticketing.Comments[0].html);

        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);

        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Single(verdictEvents);
        Assert.Equal(2, Convert.ToInt32(verdictEvents[0].Data["checks_failed_count"]));
    }

    [Fact]
    public async Task RunAsync_FailVerdict_PostsCommentNoTransition()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Fail, "fundamental issue", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Fail, result.Verdict);

        Assert.Single(ticketing.Comments);
        Assert.Contains("fail", ticketing.Comments[0].html);

        Assert.Empty(ticketing.Transitions);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.StateTransition));
    }

    [Fact]
    public async Task RunAsync_TicketNotInReview_ReturnsFailureNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("InReview", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task RunAsync_WorktreeMissing_ReturnsFailureNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: false);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("feature worktree not found", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task RunAsync_NoImplementedAtMarker_ReturnsFailureNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        // no seeded comments
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no implemented_at marker", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task RunAsync_VerifierWorkerFailedInternally_MapsToFailVerdictNoException()
    {
        // Per B.06 brief, ClaudeCodeReviewer maps worker failure to Verdict(Fail, "verifier worker failed: ...", []).
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Fail, "verifier worker failed: boom", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Fail, result.Verdict);
        Assert.Empty(ticketing.Transitions);
        Assert.Single(ticketing.Comments);
        Assert.Contains("fail", ticketing.Comments[0].html);
        Assert.Contains("verifier worker failed", ticketing.Comments[0].html);
    }

    [Fact]
    public async Task InterfaceRunAsync_PassPath_ReturnsPhaseResultWithThreeOutputs()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "looks good", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        IWorkflowPhase iface = phase;
        var result = await iface.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Phase.Review, result.Phase);
        Assert.Equal(TicketId, result.TicketId);
        Assert.Null(result.FailureReason);
        Assert.Equal(3, result.Outputs.Count);
        Assert.Equal("Pass", result.Outputs["verdict"]);
        Assert.Equal("looks good", result.Outputs["rationale"]);
        Assert.Equal("0", result.Outputs["checks_failed_count"]);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedComment(string html) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));

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
            Task.FromResult((IReadOnlyList<TicketComment>)_seededComments);
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        public string Name => "fake-verifier";
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(new WorkerResult(Status.Ok, "noop", Array.Empty<string>(), null,
                new Dictionary<string, object>()));
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

    private sealed class FakeVerifier : IVerifier
    {
        private readonly Verdict _verdict;
        public FakeVerifier(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct) =>
            Task.FromResult(_verdict);
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _mainSha;
        private readonly bool _includeWorktreeMatching;

        public FakeGitClient(string mainSha, bool includeWorktreeMatching)
        {
            _mainSha = mainSha;
            _includeWorktreeMatching = includeWorktreeMatching;
        }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);

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
            Task.FromResult("deadbeef");

        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, new[]
            {
                new DiffEntry("src/Foo.cs", DiffKind.Modified, null, 5, 2, includePatchContent ? "@@ patch @@" : null)
            }));

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
    }
}
