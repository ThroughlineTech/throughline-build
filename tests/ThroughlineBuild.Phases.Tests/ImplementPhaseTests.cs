using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ImplementPhaseTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket(TicketState state, string descriptionHtml = "<p>plan</p>") => new Ticket(
        Id: "TLB-1",
        Uuid: "ticket-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: descriptionHtml,
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-1",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static WorkerResult OkWorkerResult(string commitSha = CommitSha) => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object>
        {
            ["commit_sha"] = commitSha,
            ["files_changed"] = new[] { "src/Foo.cs" }
        });

    // A clean-exit-but-no-WORKER_RESULT failure, as the vendor agents now tag it (TLB-471).
    private static WorkerResult EnvelopeMissingResult() => new WorkerResult(
        Status.Failed, "No WORKER_RESULT found in output", Array.Empty<string>(),
        "Envelope result did not contain a WORKER_RESULT block. Stderr: ",
        new Dictionary<string, object>
        {
            [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissing
        });

    // A clean-exit worker that emitted a WORKER_RESULT envelope whose valid-JSON payload omitted
    // the required 'status' field, as the vendor agents now tag it (TLB-476).
    private static WorkerResult EnvelopeMissingStatusResult() => new WorkerResult(
        Status.Failed, "Failed to deserialize WORKER_RESULT JSON", Array.Empty<string>(),
        "Failed to deserialize WORKER_RESULT JSON: ValidationError: WORKER_RESULT JSON missing required 'status' field. Payload: {\"outcome\":\"Completed\"}",
        new Dictionary<string, object>
        {
            [WorkerResultMetadata.EnvelopeStatusKey] = WorkerResultMetadata.EnvelopeMissingStatus
        });

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsSuccessAndPostsImplementedAtComment()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Equal("ticket/tlb-1", result.BranchName);
        Assert.NotNull(result.WorktreePath);
        Assert.Null(result.FailureReason);

        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[1].state);

        Assert.Single(ticketing.Comments);
        Assert.Contains("implemented_at", ticketing.Comments[0].html);
        Assert.Contains(CommitSha, ticketing.Comments[0].html);
        Assert.Contains("ticket/tlb-1", ticketing.Comments[0].html);

        Assert.Equal(1, git.CreateWorktreeCalls);

        var ticketWrites = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        Assert.Single(ticketWrites);
        var stateTransitions = events.Events.Where(e => e.Kind == EventKind.StateTransition).ToList();
        Assert.Equal(2, stateTransitions.Count);
    }

    [Fact]
    public async Task RunAsync_TicketIsParent_ReturnsFailureWithNoSideEffects()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        ticketing.SeedChildren(new[] { MakeTicket(TicketState.Backlog), MakeTicket(TicketState.Backlog), MakeTicket(TicketState.Backlog) });
        var worker = new FakeWorkerAgent(new WorkerResult(Status.Ok, "ok", Array.Empty<string>(), null, new Dictionary<string, object> { ["commit_sha"] = "abc" }));
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("TLB-1", result.FailureReason);
        Assert.Contains("3", result.FailureReason);
        Assert.Contains("children", result.FailureReason);
        Assert.Empty(ticketing.Transitions);
        Assert.Equal(0, git.CreateWorktreeCalls);
        Assert.False(worker.WasInvoked);
    }

    [Fact]
    public async Task RunAsync_TicketNotInReady_ReturnsFailureNoTransitions()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("build plan", result.FailureReason ?? "");
        Assert.Contains("Backlog", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
        Assert.Empty(events.Events);
        Assert.Equal(0, git.CreateWorktreeCalls);
    }

    [Fact]
    public async Task RunAsync_WorktreeCreateFails_ReturnsFailureNoTransitions()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha) { CreateWorktreeFailure = "worktree path exists" };
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("worktree create failed", result.FailureReason ?? "");
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_WorkerFailed_LeavesTicketInProgressAndReturnsFailure()
    {
        var failedResult = new WorkerResult(Status.Failed, "boom", Array.Empty<string>(), "worker exploded",
            new Dictionary<string, object>());
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(failedResult);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_WorkerOkButNoCommitSha_LeavesTicketInProgressAndReturnsFailure()
    {
        var noShaResult = new WorkerResult(Status.Ok, "done", Array.Empty<string>(), null,
            new Dictionary<string, object>());
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(noShaResult);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("commit_sha", result.FailureReason ?? "");
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
    }

    [Fact]
    public async Task RunAsync_WorkerOmittedEnvelopeButCommitted_SalvagesToInReview()
    {
        // The worker ran a clean session that committed real work but ended without the
        // WORKER_RESULT envelope. HEAD (CommitSha) is ahead of base (MainSha) and the tree
        // is clean -> ImplementPhase salvages instead of discarding 40 minutes of work.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Null(result.FailureReason);

        // Transitioned InProgress then InReview, just like a normal Ok implement.
        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[1].state);

        // implemented_at comment posted, carrying the real HEAD and a recovery note.
        Assert.Single(ticketing.Comments);
        Assert.Contains("implemented_at", ticketing.Comments[0].html);
        Assert.Contains(CommitSha, ticketing.Comments[0].html);
        Assert.Contains("recovered", ticketing.Comments[0].html);

        // Recovery is surfaced as a diagnostic event.
        var recovered = events.Events
            .Where(e => e.Kind == EventKind.GateFailure && e.Data["kind"].ToString() == "implement_envelope_recovered")
            .ToList();
        Assert.Single(recovered);
    }

    [Fact]
    public async Task RunAsync_WorkerFailedWithoutEnvelopeFlag_DoesNotSalvage()
    {
        // An explicit Failed (no envelope_status flag) is a real failure - never salvaged.
        var failed = new WorkerResult(Status.Failed, "boom", Array.Empty<string>(), "worker exploded",
            new Dictionary<string, object>());
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(failed);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.Comments);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Empty(events.Events.Where(e =>
            e.Kind == EventKind.GateFailure && e.Data["kind"].ToString() == "implement_envelope_recovered"));
    }

    [Fact]
    public async Task RunAsync_EnvelopeMissingButDirtyTree_DoesNotSalvage()
    {
        // Envelope missing but the worktree has uncommitted changes -> the session did not
        // finish; do not promote it to review.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        git.TrackedChangesQueue.Enqueue(new[] { "src/Foo.cs" });
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_EnvelopeMissingButNoNewCommits_DoesNotSalvage()
    {
        // Envelope missing and HEAD is still at the base ref -> nothing was committed to review.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, MainSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_WorkerEmittedEnvelopeMissingStatusButCommitted_SalvagesToInReview()
    {
        // TLB-476: the worker emitted a WORKER_RESULT envelope whose valid-JSON payload omitted the
        // required 'status' field (e.g. {"outcome":"Completed",...}) but committed real work. HEAD is
        // ahead of base and the tree is clean -> salvage instead of discarding the branch.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingStatusResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Null(result.FailureReason);

        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.InProgress, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.InReview, ticketing.Transitions[1].state);

        // implemented_at comment posted, carrying the real HEAD and the missing-status recovery note.
        Assert.Single(ticketing.Comments);
        Assert.Contains("implemented_at", ticketing.Comments[0].html);
        Assert.Contains(CommitSha, ticketing.Comments[0].html);
        Assert.Contains("recovered", ticketing.Comments[0].html);
        Assert.Contains("missing the required status field", ticketing.Comments[0].html);

        var recovered = events.Events
            .Where(e => e.Kind == EventKind.GateFailure && e.Data["kind"].ToString() == "implement_envelope_recovered")
            .ToList();
        Assert.Single(recovered);
    }

    [Fact]
    public async Task RunAsync_EnvelopeMissingStatusButDirtyTree_DoesNotSalvage()
    {
        // Missing-status envelope but the worktree has uncommitted changes -> the session did not
        // finish; do not promote it to review.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingStatusResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        git.TrackedChangesQueue.Enqueue(new[] { "src/Foo.cs" });
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_EnvelopeMissingStatusButNoNewCommits_DoesNotSalvage()
    {
        // Missing-status envelope and HEAD is still at the base ref -> nothing was committed.
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(EnvelopeMissingStatusResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, MainSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.Comments);
    }

    [Fact]
    public async Task RunAsync_DriftDetected_EmitsGateFailureWithKindDriftWarningButProceeds()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        ticketing.SeedComment("<p>[planned_at: stale-sha-1111111111111111111111111111111111111111]</p>");
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("drift_warning", gateFailures[0].Data["kind"].ToString());
    }

    [Fact]
    public async Task RunAsync_NoDriftMarker_DoesNotEmitGateFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(events.Events.Where(e => e.Kind == EventKind.GateFailure));
    }

    [Fact]
    public async Task RunAsync_WithSummaryRef_CommentIncludesRenderedSummaryHtml()
    {
        const string summaryMarkdown = "# Summary\n\nRan `dotnet build` successfully.";
        var metadata = new Dictionary<string, object>
        {
            ["commit_sha"] = CommitSha,
            ["files_changed"] = new[] { "src/Foo.cs" },
            ["summary_ref"] = "IMPLEMENT_SUMMARY"
        };
        var blocks = new Dictionary<string, string>
        {
            ["IMPLEMENT_SUMMARY"] = summaryMarkdown
        };
        var workerResult = new WorkerResult(Status.Ok, "implemented", new[] { "src/Foo.cs" }, null, metadata, blocks);

        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(workerResult);
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        var html = ticketing.Comments[0].html;
        Assert.Contains("implemented_at", html);
        Assert.Contains("<h1>Summary</h1>", html);
        Assert.Contains("<code>dotnet build</code>", html);
    }

    [Fact]
    public async Task RunAsync_WithoutSummaryRef_CommentContainsOnlyImplementedAtMarker()
    {
        // Backward-compat: no summary_ref in metadata -> comment is only the implemented_at line
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(ticketing.Comments);
        var html = ticketing.Comments[0].html;
        Assert.Contains("implemented_at", html);
        // No extra HTML beyond the single <p> tag
        Assert.Equal($"<p>[implemented_at: {CommitSha}] (branch ticket/tlb-1)</p>", html);
    }

    [Fact]
    public async Task RunAsync_HeadShaDiffersFromMetadata_PrefersActualHeadAndNotesDiscrepancy()
    {
        const string ActualHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string ReportedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult(ReportedSha));
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, ActualHead);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ActualHead, result.CommitSha);
        Assert.Single(ticketing.Comments);
        Assert.Contains(ActualHead, ticketing.Comments[0].html);
        Assert.Contains(ReportedSha, ticketing.Comments[0].html);
    }

    [Fact]
    public async Task InterfaceRunAsync_HappyPath_ReturnsPhaseResultWithThreeOutputs()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        IWorkflowPhase iface = phase;
        var result = await iface.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Phase.Implement, result.Phase);
        Assert.Equal("TLB-1", result.TicketId);
        Assert.Null(result.FailureReason);
        Assert.Equal(3, result.Outputs.Count);
        Assert.Equal(CommitSha, result.Outputs["commit_sha"]);
        Assert.Equal("ticket/tlb-1", result.Outputs["branch"]);
        Assert.NotNull(result.Outputs["worktree_path"]);
    }

    [Fact]
    public void ImplementPhase_IsAssignableTo_IWorkflowPhase_AndExposesImplementPhase()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        IWorkflowPhase iface = phase;
        Assert.Equal(Phase.Implement, iface.Phase);
    }

    [Fact]
    public async Task RunAsync_StateCheckFailure_WritesEarlyExitManifest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var captureDir = Path.Combine(tempRoot, "sessions", Guid.NewGuid().ToString("N"));
        try
        {
            var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
            var worker = new FakeWorkerAgent(OkWorkerResult());
            var events = new FakeEventSink();
            var git = new FakeGitClient(MainSha, CommitSha);
            var options = new BuildOptions("session-state-fail", "claude-code", TimeSpan.FromMinutes(5),
                DebugCaptureDirectory: captureDir);
            var phase = new ImplementPhase(ticketing, worker, events, options, git);

            await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

            var manifestPath = Path.Combine(captureDir, "phase-status.json");
            Assert.True(File.Exists(manifestPath), $"Expected phase-status.json at {manifestPath}");

            var content = File.ReadAllText(manifestPath);
            Assert.Contains("Backlog", content);
            Assert.Contains("build plan", content);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_GitRevParseFails_WritesEarlyExitManifest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var captureDir = Path.Combine(tempRoot, "sessions", Guid.NewGuid().ToString("N"));
        try
        {
            var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
            var worker = new FakeWorkerAgent(OkWorkerResult());
            var events = new FakeEventSink();
            var git = new FakeGitClient(MainSha, CommitSha) { RevParseFailure = "ref not found" };
            var options = new BuildOptions("session-rev-parse-fail", "claude-code", TimeSpan.FromMinutes(5),
                DebugCaptureDirectory: captureDir);
            var phase = new ImplementPhase(ticketing, worker, events, options, git);

            await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

            var manifestPath = Path.Combine(captureDir, "phase-status.json");
            Assert.True(File.Exists(manifestPath), $"Expected phase-status.json at {manifestPath}");

            var content = File.ReadAllText(manifestPath);
            Assert.Contains("git rev-parse failed", content);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WorktreeCreateFails_WritesEarlyExitManifest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var captureDir = Path.Combine(tempRoot, "sessions", Guid.NewGuid().ToString("N"));
        try
        {
            var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
            var worker = new FakeWorkerAgent(OkWorkerResult());
            var events = new FakeEventSink();
            var git = new FakeGitClient(MainSha, CommitSha) { CreateWorktreeFailure = "worktree path exists" };
            var options = new BuildOptions("session-worktree-fail", "claude-code", TimeSpan.FromMinutes(5),
                DebugCaptureDirectory: captureDir);
            var phase = new ImplementPhase(ticketing, worker, events, options, git);

            await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

            var manifestPath = Path.Combine(captureDir, "phase-status.json");
            Assert.True(File.Exists(manifestPath), $"Expected phase-status.json at {manifestPath}");

            var content = File.ReadAllText(manifestPath);
            Assert.Contains("worktree create failed", content);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
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

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;

        private List<Ticket> _children = new();
        public void SeedChildren(IReadOnlyList<Ticket> children) => _children = children.ToList();
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(_children.AsReadOnly());

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
        public bool WasInvoked { get; private set; }
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(_result);
        }
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
        private readonly string _mainSha;
        private readonly string _headSha;
        public int CreateWorktreeCalls { get; private set; }
        public string? CreateWorktreeFailure { get; set; }
        public string? RevParseFailure { get; set; }

        public FakeGitClient(string mainSha, string headSha)
        {
            _mainSha = mainSha;
            _headSha = headSha;
        }

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct)
        {
            if (RevParseFailure is not null)
                throw new InvalidOperationException(RevParseFailure);
            return Task.FromResult(_mainSha);
        }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            CreateWorktreeCalls++;
            return Task.FromResult(CreateWorktreeFailure is null
                ? new WorktreeCreateResult(true, null, worktreePath)
                : new WorktreeCreateResult(false, CreateWorktreeFailure, null));
        }

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
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        // Configurable dirty-paths responses for dirty-worktree tests.
        // If empty (default), returns the interface default (empty list).
        public Queue<IReadOnlyList<string>> TrackedChangesQueue { get; } = new();
        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
        {
            if (TrackedChangesQueue.Count > 0)
                return Task.FromResult(TrackedChangesQueue.Dequeue());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    // Worker that returns results from a pre-loaded queue - used for dirty-worktree retry tests.
    private sealed class QueuedFakeWorkerAgent : IWorkerAgent
    {
        private readonly Queue<WorkerResult> _results;
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public QueuedFakeWorkerAgent(IEnumerable<WorkerResult> results)
        {
            _results = new Queue<WorkerResult>(results);
        }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
            => Task.FromResult(_results.Dequeue());
    }
}

public class ImplementPhaseDirtyWorktreeTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket() => new Ticket(
        Id: "TLB-1", Uuid: "ticket-uuid-1", Title: "Test ticket", Type: "feature", State: TicketState.Ready,
        Size: Size.S, Risk: Risk.Low, DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(), Labels: Array.Empty<string>(), ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-1", WorkerName: "claude-code", WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object> { ["commit_sha"] = CommitSha });

    [Fact]
    public async Task RunAsync_DirtyWorktreeAfterOk_TriggersRetry_CleanOnRetry_Succeeds()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        // First worker call is Ok; so is the retry
        var worker = new QueuedFakeWorkerAgent(new[] { OkWorkerResult(), OkWorkerResult() });
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        // First dirty check: dirty. Second dirty check (after retry): clean.
        git.TrackedChangesQueue.Enqueue(new[] { "src/Foo.cs" });
        git.TrackedChangesQueue.Enqueue(Array.Empty<string>());
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CommitSha, result.CommitSha);
        Assert.Null(result.FailureReason);

        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Single(gateFailures);
        Assert.Equal("dirty_worktree_first_attempt", gateFailures[0].Data["kind"].ToString());
        Assert.True(gateFailures[0].Data.ContainsKey("dirty_paths"));

        // Two VerifierVerdict events: initial worker + retry worker
        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.Equal(2, verdictEvents.Count);
    }

    [Fact]
    public async Task RunAsync_DirtyWorktreeAfterOk_RetryStillDirty_Fails()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new QueuedFakeWorkerAgent(new[] { OkWorkerResult(), OkWorkerResult() });
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        // Both dirty checks return the same dirty file
        git.TrackedChangesQueue.Enqueue(new[] { "src/Foo.cs" });
        git.TrackedChangesQueue.Enqueue(new[] { "src/Foo.cs" });
        var phase = new ImplementPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("dirty", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Foo.cs", result.FailureReason);

        var gateFailures = events.Events.Where(e => e.Kind == EventKind.GateFailure).ToList();
        Assert.Equal(2, gateFailures.Count);
        Assert.Equal("dirty_worktree_first_attempt", gateFailures[0].Data["kind"].ToString());
        Assert.Equal("dirty_worktree_retry_failed", gateFailures[1].Data["kind"].ToString());
    }

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
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) { Events.Add(ev); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _mainSha;
        private readonly string _headSha;
        public int CreateWorktreeCalls { get; private set; }
        public Queue<IReadOnlyList<string>> TrackedChangesQueue { get; } = new();
        public FakeGitClient(string mainSha, string headSha) { _mainSha = mainSha; _headSha = headSha; }
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
            CreateWorktreeCalls++;
            return Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        }
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) => Task.FromResult(_headSha);
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
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct)
        {
            if (TrackedChangesQueue.Count > 0)
                return Task.FromResult(TrackedChangesQueue.Dequeue());
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private sealed class QueuedFakeWorkerAgent : IWorkerAgent
    {
        private readonly Queue<WorkerResult> _results;
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public QueuedFakeWorkerAgent(IEnumerable<WorkerResult> results) { _results = new Queue<WorkerResult>(results); }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
            => Task.FromResult(_results.Dequeue());
    }
}

public class ImplementPhaseDebugCaptureTests
{
    private const string MainSha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitSha = "ffffffffffffffffffffffffffffffffffffffff";

    private static Ticket MakeTicket() => new Ticket(
        Id: "TLB-1", Uuid: "ticket-uuid-1", Title: "Test ticket", Type: "feature", State: TicketState.Ready,
        Size: Size.S, Risk: Risk.Low, DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(), Labels: Array.Empty<string>(), ParentId: null);

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "implemented", new[] { "src/Foo.cs" }, null,
        new Dictionary<string, object> { ["commit_sha"] = CommitSha });

    [Fact]
    public async Task RunAsync_DebugCaptureDirectorySet_ForwardedToWorkerOptions()
    {
        const string captureDir = "/tmp/impl-debug-capture-test";
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new CapturingWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var options = new BuildOptions("session-dbg", "claude-code", TimeSpan.FromMinutes(5),
            DebugCaptureDirectory: captureDir);
        var phase = new ImplementPhase(ticketing, worker, events, options, git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.NotNull(worker.LastOptions);
        Assert.Equal(captureDir, worker.LastOptions!.DebugCaptureDirectory);
    }

    [Fact]
    public async Task RunAsync_DebugCaptureDirectoryNull_WorkerOptionsHasNullDirectory()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new CapturingWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha, CommitSha);
        var options = new BuildOptions("session-1", "claude-code", TimeSpan.FromMinutes(5));
        var phase = new ImplementPhase(ticketing, worker, events, options, git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.NotNull(worker.LastOptions);
        Assert.Null(worker.LastOptions!.DebugCaptureDirectory);
    }

    [Fact]
    public async Task RunAsync_DebugCaptureDirectorySet_DirectoryCreatedOnDisk()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var captureDir = Path.Combine(tempRoot, "sessions", Guid.NewGuid().ToString("N"));
        try
        {
            var ticketing = new FakeTicketing(MakeTicket());
            var worker = new CapturingWorkerAgent(OkWorkerResult());
            var events = new FakeEventSink();
            var git = new FakeGitClient(MainSha, CommitSha);
            var options = new BuildOptions("session-disk-test", "claude-code", TimeSpan.FromMinutes(5),
                DebugCaptureDirectory: captureDir);
            var phase = new ImplementPhase(ticketing, worker, events, options, git);

            await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

            Assert.True(Directory.Exists(captureDir),
                $"Expected debug capture directory to be created at {captureDir}");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class CapturingWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public WorkerOptions? LastOptions { get; private set; }
        public CapturingWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastOptions = options;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public FakeTicketing(Ticket ticket) { _ticket = ticket; }
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
        private readonly string _headSha;
        public FakeGitClient(string mainSha, string headSha) { _mainSha = mainSha; _headSha = headSha; }
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_mainSha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
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
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
    }
}
