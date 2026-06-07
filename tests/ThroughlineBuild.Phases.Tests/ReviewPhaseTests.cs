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
    private const string BranchName = "ticket/tlb-1";

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
    public async Task RunAsync_MultipleImplementedAtMarkers_ReconstructsFromFreshestByTimestamp()
    {
        // Regression (TLB-412): a chain re-run leaves stale implemented_at markers from prior
        // runs on the ticket. Plane returns comments newest-first, so the old "keep last in list
        // order" scan reconstructed from the OLDEST (stale, orphaned) commit. Seed the stale
        // marker LAST in list order but with an older timestamp; the freshest must still win.
        const string staleSha = "1111111111111111111111111111111111111111";
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        var now = DateTimeOffset.UtcNow;
        ticketing.SeedCommentAt($"<p>[implemented_at: {ImplementedSha}]</p>", now);            // fresh, first in list
        ticketing.SeedCommentAt($"<p>[implemented_at: {staleSha}]</p>", now.AddDays(-1));       // stale, last in list
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(verifier.LastWorkerResult);
        Assert.Equal(ImplementedSha, verifier.LastWorkerResult!.Metadata["commit_sha"]);
        Assert.NotEqual(staleSha, verifier.LastWorkerResult!.Metadata["commit_sha"]);
    }

    [Fact]
    public async Task RunAsync_MarkerSupersededByWorktreeHead_AttributesToHeadAndEmitsDrift()
    {
        // Regression (TLB-414): the implementer amended/squashed after posting [implemented_at],
        // so the freshest marker points at a now-superseded commit while the worktree HEAD (which
        // the checks and diff actually ran against) has moved on. Review must attribute to HEAD and
        // surface the drift, not reason about the orphaned marker commit.
        const string amendedHead = "abcabcabcabcabcabcabcabcabcabcabcabcabca";
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true, headSha: amendedHead);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        // Attribution follows ground-truth HEAD, not the superseded marker.
        Assert.Equal(amendedHead, verifier.LastWorkerResult!.Metadata["commit_sha"]);
        Assert.NotEqual(ImplementedSha, verifier.LastWorkerResult!.Metadata["commit_sha"]);
        // Drift is surfaced as an event carrying both SHAs.
        var drift = events.Events.Single(e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && (k?.ToString() == "implemented_at_superseded"));
        Assert.Equal(ImplementedSha, drift.Data["marker_sha"]);
        Assert.Equal(amendedHead, drift.Data["head_sha"]);
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
    public async Task RunAsync_WorktreeMissingButBranchExists_ReconstructsWorktreeAndReviews()
    {
        // A parent chain removes its shared worktree at chain end, leaving an InReview child with a
        // branch but no worktree. Re-running review must reconstruct the worktree from the branch
        // instead of dead-ending at "feature worktree not found".
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: false, branchExistsForRecovery: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "looks good", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);
        Assert.Null(result.FailureReason);
        Assert.True(git.CheckoutWorktreeCalled, "review should reconstruct the missing worktree from the branch");
        Assert.Single(ticketing.Comments);
        Assert.Contains("pass", ticketing.Comments[0].html);
    }

    [Fact]
    public async Task RunAsync_WorktreeMissingAndBranchMissing_ReturnsFailureNoSideEffects()
    {
        // No branch to reconstruct from -> still a clean failure (no checkout attempt).
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: false, branchExistsForRecovery: false);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("feature worktree not found", result.FailureReason ?? "");
        Assert.False(git.CheckoutWorktreeCalled);
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
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

    [Fact]
    public async Task RunAsync_VerifierWorkerReportsLlmUsage_EmitsOneLlmCallEvent()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");

        // Create worker metadata with both verdict and llm_usage
        var llmUsageMetadata = new Dictionary<string, object>
        {
            ["model"] = "claude-opus",
            ["input_tokens"] = 1500,
            ["output_tokens"] = 500,
            ["cache_read_tokens"] = 200,
            ["cache_create_tokens"] = 100,
            ["wall_clock_ms"] = 2500
        };
        var workerMetadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "looks good",
            ["checks_failed"] = new List<string>(),
            ["llm_usage"] = llmUsageMetadata
        };

        var worker = new FakeWorkerAgent(workerMetadata);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);

        // DO NOT use verifierOverride so ReviewPhase constructs a real ClaudeCodeReviewer
        var verifierWorker = new FakeWorkerAgent(workerMetadata);
        var reviewOptions = new ReviewOptions(
            Checks: Array.Empty<CheckSpec>(),
            VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null, null));

        var phase = new ReviewPhase(ticketing, verifierWorker, events, MakeBuildOptions(), reviewOptions, git);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);

        // Assert exactly one LlmCall event with expected Data keys
        var llmCallEvents = events.Events.Where(e => e.Kind == EventKind.LlmCall).ToList();
        Assert.Single(llmCallEvents);

        var llmCallData = llmCallEvents[0].Data;
        Assert.Equal("claude-opus", llmCallData["model"].ToString());
        Assert.Equal(1500, Convert.ToInt32(llmCallData["input_tokens"]));
        Assert.Equal(500, Convert.ToInt32(llmCallData["output_tokens"]));
        Assert.Equal(200, Convert.ToInt32(llmCallData["cache_read_tokens"]));
        Assert.Equal(100, Convert.ToInt32(llmCallData["cache_create_tokens"]));
        Assert.Equal(2500, Convert.ToInt32(llmCallData["wall_clock_ms"]));
    }

    [Fact]
    public async Task RunAsync_ReworkVerdict_VerifierVerdictEventContainsRationaleAndChecksFailed()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Rework, "needs tests", new[] { "unit-tests", "lint" }));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Rework, result.Verdict);

        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Single(verdictEvents);

        var data = verdictEvents[0].Data;
        Assert.Equal("Rework", data["kind"].ToString());
        Assert.Equal("needs tests", data["rationale"].ToString());
        Assert.True(data.ContainsKey("checks_failed"), "VerifierVerdict Data must contain checks_failed");
        var checksFailed = data["checks_failed"] as IReadOnlyList<string>;
        Assert.NotNull(checksFailed);
        Assert.Equal(new[] { "unit-tests", "lint" }, checksFailed);
    }

    [Fact]
    public async Task RunAsync_PassVerdict_VerifierVerdictEventContainsRationaleAndEmptyChecksFailed()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "all good", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);

        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Single(verdictEvents);

        var data = verdictEvents[0].Data;
        Assert.Equal("Pass", data["kind"].ToString());
        Assert.Equal("all good", data["rationale"].ToString());
        Assert.True(data.ContainsKey("checks_failed"), "VerifierVerdict Data must contain checks_failed");
    }

    [Fact]
    public async Task RunAsync_VerifierWorkerHasNoLlmUsage_NoLlmCallEventEmitted()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");

        // Create worker metadata with verdict but NO llm_usage
        var workerMetadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "looks good",
            ["checks_failed"] = new List<string>()
        };

        var worker = new FakeWorkerAgent(workerMetadata);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);

        // DO NOT use verifierOverride so ReviewPhase constructs a real ClaudeCodeReviewer
        var verifierWorker = new FakeWorkerAgent(workerMetadata);
        var reviewOptions = new ReviewOptions(
            Checks: Array.Empty<CheckSpec>(),
            VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null, null));

        var phase = new ReviewPhase(ticketing, verifierWorker, events, MakeBuildOptions(), reviewOptions, git);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);

        // Assert zero LlmCall events
        var llmCallEvents = events.Events.Where(e => e.Kind == EventKind.LlmCall).ToList();
        Assert.Empty(llmCallEvents);
    }

    [Fact]
    public async Task RunAsync_ParentTicket_AllChildrenDone_ReturnsPass()
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
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Pass, result.Verdict);
        Assert.Contains("Done", result.VerdictRationale ?? "");
        Assert.Null(result.FailureReason);
        Assert.Single(ticketing.Comments);
        Assert.Contains("pass", ticketing.Comments[0].html);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_ParentTicket_AnyChildInProgress_ReturnsRework()
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
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Rework, result.Verdict);
        Assert.Contains("TLB-3", result.VerdictRationale ?? "");
        Assert.Single(ticketing.Comments);
        Assert.Contains("rework", ticketing.Comments[0].html);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_ParentTicket_AnyChildCancelled_ReturnsFail()
    {
        var ticket = MakeTicket(TicketState.InReview);
        var ticketing = new FakeTicketing(ticket);
        ticketing.SeedChildren(new[]
        {
            new Ticket("TLB-2", "child-uuid-2", "Child A", "feature", TicketState.Done,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid),
            new Ticket("TLB-3", "child-uuid-3", "Child B", "feature", TicketState.Cancelled,
                Size.S, Risk.Low, "", Array.Empty<Relation>(), Array.Empty<string>(), ticket.Uuid)
        });
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerdictKind.Fail, result.Verdict);
        Assert.Contains("TLB-3", result.VerdictRationale ?? "");
        Assert.Single(ticketing.Comments);
        Assert.Contains("fail", ticketing.Comments[0].html);
        Assert.Empty(ticketing.Transitions);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        private List<Ticket> _queryChildren = new();
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public void SeedComment(string html) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));

        public void SeedCommentAt(string html, DateTimeOffset createdAt) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, createdAt));

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
            Task.FromResult((IReadOnlyList<TicketComment>)_seededComments);

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

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public FakeWorkerAgent(IReadOnlyDictionary<string, object>? metadata = null)
        {
            _metadata = metadata;
        }

        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(new WorkerResult(Status.Ok, "noop", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>()));
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
        public WorkerResult? LastWorkerResult { get; private set; }
        public FakeVerifier(Verdict verdict) { _verdict = verdict; }
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct)
        {
            LastWorkerResult = workerResult;
            return Task.FromResult(_verdict);
        }
    }

    private sealed class CapturingWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        public WorkerOptions? LastOptions { get; private set; }
        public string? LastWorkingDirectory { get; private set; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public CapturingWorkerAgent(IReadOnlyDictionary<string, object>? metadata = null) { _metadata = metadata; }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastOptions = options;
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(new WorkerResult(Status.Ok, "noop", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>()));
        }
    }

    [Fact]
    public async Task RunAsync_VerifierWorkerRunsInWorktreeNotMainDirectory()
    {
        // Verifier must run in the feature worktree, not workingDirectory.
        // Running in main dirties tracked files and blocks ShipPhase's pre-flight dirty check.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "looks good",
            ["checks_failed"] = new List<string>()
        };
        var capturingWorker = new CapturingWorkerAgent(metadata);
        var phase = new ReviewPhase(ticketing, capturingWorker, events, MakeBuildOptions(), MakeReviewOptions(), git);
        var mainDir = MakeWorkingDir();

        var result = await phase.RunAsync(TicketId, mainDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturingWorker.LastWorkingDirectory);
        Assert.NotEqual(mainDir, capturingWorker.LastWorkingDirectory);
        Assert.Equal("/some/worktree/path", capturingWorker.LastWorkingDirectory);
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _mainSha;
        private readonly bool _includeWorktreeMatching;
        private readonly bool _branchExistsForRecovery;

        // Mutable so a verifier test double can simulate moving HEAD mid-review (TLB-478 guard).
        public string CurrentHeadSha;
        // Mutable repo-global stash stack; StashDropAsync pops the top (index 0).
        public List<string> StashEntries { get; } = new();
        public int StashDropCount { get; private set; }

        public bool CheckoutWorktreeCalled { get; private set; }

        // headSha defaults to the implementer marker SHA so the worktree HEAD matches the
        // [implemented_at] marker in the normal case (no drift). Override it to simulate an
        // implementer that amended/squashed after posting the marker (TLB-414).
        public FakeGitClient(string mainSha, bool includeWorktreeMatching, bool branchExistsForRecovery = false, string? headSha = null)
        {
            _mainSha = mainSha;
            _includeWorktreeMatching = includeWorktreeMatching;
            _branchExistsForRecovery = branchExistsForRecovery;
            CurrentHeadSha = headSha ?? ImplementedSha;
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

        public Task<IReadOnlyList<string>> ListLocalBranchesAsync(string pattern, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _branchExistsForRecovery ? new[] { BranchName } : Array.Empty<string>());

        public Task<WorktreeCreateResult> CheckoutWorktreeAsync(string worktreePath, string existingBranch, string mainWorktreePath, CancellationToken ct)
        {
            CheckoutWorktreeCalled = true;
            return Task.FromResult(new WorktreeCreateResult(true, null, "/some/worktree/path"));
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(CurrentHeadSha);

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(StashEntries.ToList());

        public Task<GitOpResult> StashDropAsync(string stashRef, string workingDirectory, CancellationToken ct)
        {
            if (StashEntries.Count > 0)
                StashEntries.RemoveAt(0); // drop the top (stash@{0})
            StashDropCount++;
            return Task.FromResult(new GitOpResult(true, null));
        }

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

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        // Configurable dirty-paths responses for dirty-worktree tests.
        public Queue<IReadOnlyList<string>> TrackedChangesQueue { get; } = new();
        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
        {
            if (TrackedChangesQueue.Count > 0)
                return Task.FromResult(TrackedChangesQueue.Dequeue());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    [Fact]
    public async Task RunAsync_DirtyWorktreeAfterVerifier_HardFails()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        git.TrackedChangesQueue.Enqueue(new[] { "review-artifact.dll" });
        var verifier = new FakeVerifier(new Verdict(VerdictKind.Pass, "looks good", Array.Empty<string>()));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("dirty", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("review-artifact.dll", result.FailureReason);

        // VerifierVerdict still emitted (operator needs to see what verdict was reached)
        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Single(verdictEvents);
        Assert.Equal("Pass", verdictEvents[0].Data["kind"].ToString());

        // GateFailure emitted with correct kind and dirty_paths
        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("dirty_worktree_after_review", gateFailures[0].Data["kind"].ToString());
        Assert.True(gateFailures[0].Data.ContainsKey("dirty_paths"));

        // No ticket transitions or comments - hard-fail stops before verdict processing
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
    }

    // A verifier override that mutates shared git state mid-review, simulating an unsandboxed
    // codex verifier that runs `git stash` or `git reset` despite the prompt ban (TLB-478).
    private sealed class MutatingVerifier : IVerifier
    {
        private readonly Verdict _verdict;
        private readonly Action _onVerify;
        public MutatingVerifier(Verdict verdict, Action onVerify) { _verdict = verdict; _onVerify = onVerify; }
        public Task<Verdict> VerifyAsync(Brief brief, GitDiff diff, WorkerResult workerResult, CancellationToken ct)
        {
            _onVerify();
            return Task.FromResult(_verdict);
        }
    }

    [Fact]
    public async Task RunAsync_VerifierPushesStash_HardFailsAndDropsIt()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        // The verifier runs `git stash` mid-review: an entry lands on the repo-global stack while
        // the worktree itself stays clean, so the Step 10b dirty check does not catch it.
        var verifier = new MutatingVerifier(
            new Verdict(VerdictKind.Pass, "looks good", Array.Empty<string>()),
            () => git.StashEntries.Insert(0, "stash@{0}: On ticket/tlb-1: WIP"));
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("shared git state", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stash", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        // The leaked stash entry was dropped and the stack restored.
        Assert.Equal(1, git.StashDropCount);
        Assert.Empty(git.StashEntries);
        // Verdict surfaced first, then a GateFailure carrying the delta/dropped counts.
        Assert.Single(events.Events.Where(e => e.Kind == EventKind.VerifierVerdict));
        var gate = events.Events.Single(e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && k?.ToString() == "shared_git_state_mutated_after_review");
        Assert.Equal(1, Convert.ToInt32(gate.Data["stash_delta"]));
        Assert.Equal(1, Convert.ToInt32(gate.Data["stash_dropped"]));
        // Hard-fail stops before verdict processing: no transition, no comment.
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_VerifierMovesHead_HardFails()
    {
        const string movedSha = "9999999999999999999999999999999999999999";
        var ticketing = new FakeTicketing(MakeTicket(TicketState.InReview));
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");
        var worker = new FakeWorkerAgent();
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);
        // The verifier runs `git reset`/`checkout` mid-review: HEAD moves off the reviewed commit
        // while leaving a clean tree, again evading the Step 10b dirty check.
        var verifier = new MutatingVerifier(
            new Verdict(VerdictKind.Pass, "ok", Array.Empty<string>()),
            () => git.CurrentHeadSha = movedSha);
        var phase = new ReviewPhase(ticketing, worker, events, MakeBuildOptions(), MakeReviewOptions(),
            git, verifierOverride: verifier);

        var result = await phase.RunAsync(TicketId, MakeWorkingDir(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HEAD moved", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        var gate = events.Events.Single(e => e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var k) && k?.ToString() == "shared_git_state_mutated_after_review");
        Assert.Equal(ImplementedSha, gate.Data["head_before"].ToString());
        Assert.Equal(movedSha, gate.Data["head_after"].ToString());
        Assert.Equal(0, Convert.ToInt32(gate.Data["stash_delta"]));
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.Comments);
    }
}

public class ReviewPhaseDebugCaptureTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ImplementedSha = "ffffffffffffffffffffffffffffffffffffffff";
    private const string TicketId = "TLB-1";
    private const string TicketTitle = "Test ticket";
    private const string BranchName = "ticket/tlb-1";

    private static Ticket MakeTicket() => new Ticket(
        Id: TicketId, Uuid: "ticket-uuid-1", Title: TicketTitle, Type: "feature", State: TicketState.InReview,
        Size: Size.S, Risk: Risk.Low, DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(), Labels: Array.Empty<string>(), ParentId: null);

    private static IReadOnlyDictionary<string, object> VerifierMetadata() => new Dictionary<string, object>
    {
        ["verdict"] = "Pass",
        ["rationale"] = "looks good",
        ["checks_failed"] = new List<string>()
    };

    [Fact]
    public async Task RunAsync_VerifierWorkerOptionsDebugCaptureSet_WorkerReceivesThatDirectory()
    {
        const string captureDir = "/tmp/review-debug-capture-test";
        var ticketing = new FakeTicketing(MakeTicket());
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");

        var verifierWorker = new CapturingWorkerAgent(VerifierMetadata());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);

        var reviewOptions = new ReviewOptions(
            Checks: Array.Empty<CheckSpec>(),
            VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null,
                DebugCaptureDirectory: captureDir));
        var buildOptions = new BuildOptions("session-rev-dbg", "claude-code", TimeSpan.FromMinutes(5));
        var phase = new ReviewPhase(ticketing, verifierWorker, events, buildOptions, reviewOptions, git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(verifierWorker.LastOptions);
        Assert.Equal(captureDir, verifierWorker.LastOptions!.DebugCaptureDirectory);
    }

    [Fact]
    public async Task RunAsync_VerifierWorkerOptionsDebugCaptureNull_WorkerReceivesNullDirectory()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        ticketing.SeedComment($"<p>[implemented_at: {ImplementedSha}]</p>");

        var verifierWorker = new CapturingWorkerAgent(VerifierMetadata());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, includeWorktreeMatching: true);

        var reviewOptions = new ReviewOptions(
            Checks: Array.Empty<CheckSpec>(),
            VerifierWorkerOptions: new WorkerOptions(TimeSpan.FromMinutes(5), null));
        var buildOptions = new BuildOptions("session-rev-1", "claude-code", TimeSpan.FromMinutes(5));
        var phase = new ReviewPhase(ticketing, verifierWorker, events, buildOptions, reviewOptions, git);

        var result = await phase.RunAsync(TicketId, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(verifierWorker.LastOptions);
        Assert.Null(verifierWorker.LastOptions!.DebugCaptureDirectory);
    }

    private sealed class CapturingWorkerAgent : IWorkerAgent
    {
        private readonly IReadOnlyDictionary<string, object>? _metadata;
        public WorkerOptions? LastOptions { get; private set; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public CapturingWorkerAgent(IReadOnlyDictionary<string, object>? metadata = null) { _metadata = metadata; }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastOptions = options;
            return Task.FromResult(new WorkerResult(Status.Ok, "noop", Array.Empty<string>(), null,
                _metadata ?? new Dictionary<string, object>()));
        }
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private readonly List<TicketComment> _seededComments = new();
        public FakeTicketing(Ticket ticket) { _ticket = ticket; }
        public void SeedComment(string html) =>
            _seededComments.Add(new TicketComment(Guid.NewGuid().ToString(), html, DateTimeOffset.UtcNow));
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Task.CompletedTask;
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => Task.FromResult("c-1");
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Relation>)Array.Empty<Relation>());
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)_seededComments);

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

    private sealed class FakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
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

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
    }
}
