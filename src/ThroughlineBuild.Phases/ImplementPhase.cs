using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

public record ImplementPhaseOptions(
    ReviewFeedback? ReviewFeedback = null,
    string? SharedWorktreePath = null);

public record ImplementResult(
    bool Success,
    string TicketId,
    string? CommitSha,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason,
    int ReworkRoundNumber = 0,
    WorkerResult? EscalationWorkerResult = null);

public class ImplementPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IGitClient _git;
    private readonly ProjectContext _project;
    private readonly ImplementPhaseOptions _phaseOptions;

    public ImplementPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IGitClient? gitClient = null,
        ProjectContext? project = null,
        ImplementPhaseOptions? phaseOptions = null)
    {
        _ticketing = ticketing;
        _worker = worker;
        _events = events;
        _options = options;
        _git = gitClient ?? new ProcessGitClient();
        _project = project ?? ProjectContext.Empty;
        _phaseOptions = phaseOptions ?? new ImplementPhaseOptions();
    }

    public Phase Phase => Phase.Implement;

    public async Task<ImplementResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Fetch ticket
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Step 1b: Parent guard - refuse to implement a parent ticket directly
        var potentialChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (potentialChildren.Count > 0)
        {
            var parentReason = $"{ticketId} is a parent ticket with {potentialChildren.Count} children: work child-by-child; implementing a parent directly is almost always a mistake.";
            EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, parentReason);
            return new ImplementResult(false, ticketId, null, null, null, parentReason);
        }

        // Step 2: Validate state
        bool isRework = _phaseOptions.ReviewFeedback is not null;
        if (!isRework && ticket.State != TicketState.Ready)
        {
            var reason = InitialRoundStateGuidance(ticketId, ticket.State);
            EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, reason);
            return new ImplementResult(false, ticketId, null, null, null, reason);
        }
        if (isRework && ticket.State != TicketState.InProgress)
        {
            var reason = $"rework round invoked but ticket is in {ticket.State} - no review has run yet";
            EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, reason);
            return new ImplementResult(false, ticketId, null, null, null, reason);
        }

        // Step 2b: Hygiene gate - refuse to proceed on a conflicted or stash-polluted tree
        var ticketBranchPrefix = $"ticket/{ticket.Id.ToLowerInvariant()}-";
        var hygieneFailure = await WorkingTreeHygieneGate.CheckAsync(_git, workingDirectory, ticketBranchPrefix, ct).ConfigureAwait(false);
        if (hygieneFailure is not null)
        {
            var hygieneReason = $"working tree is not clean: {hygieneFailure}";
            EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, hygieneReason);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "hygiene_gate",
                ["detail"] = hygieneFailure
            }, ct).ConfigureAwait(false);
            return new ImplementResult(false, ticketId, null, null, null, hygieneReason);
        }

        // Step 3: Resolve base ref (origin/main with fallback to local main) and its SHA
        string baseRef;
        string mainSha;
        try
        {
            (baseRef, mainSha) = await BaseRefResolver.ResolveAsync(_git, workingDirectory, _options.TargetBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failureReason = $"git rev-parse failed: {ex.Message}";
            EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, failureReason);
            return new ImplementResult(false, ticketId, null, null, null, failureReason);
        }

        // Step 4: Compute worktree names
        var worktreeNames = PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, workingDirectory);

        // Step 5: Drift check - scan comments for [planned_at: <sha>]
        var comments = await _ticketing.GetCommentsAsync(ticketId, ct).ConfigureAwait(false);
        string? plannedAtSha = null;
        foreach (var comment in comments)
        {
            var markers = MarkerParser.Parse(comment.Body);
            foreach (var m in markers)
            {
                if (m.Name == "planned_at" && !string.IsNullOrEmpty(m.Value))
                {
                    plannedAtSha = m.Value;
                    break;
                }
            }
            if (plannedAtSha is not null) break;
        }
        if (plannedAtSha is not null && plannedAtSha != mainSha)
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "drift_warning",
                ["planned_at_sha"] = plannedAtSha,
                ["main_sha"] = mainSha
            }, ct).ConfigureAwait(false);
        }

        // Step 6: Build RepoState
        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);

        // Step 7: Resolve canonical worktree.
        // Shared-worktree path: the chain pre-created a worktree; each ticket creates its own
        // branch inside it using CreateBranchAsync.
        // Rework path: locate the existing worktree by branch or path.
        // Initial path (no shared worktree): CreateWorktreeAsync below.
        bool isSharedWorktree = _phaseOptions.SharedWorktreePath is not null;
        string canonicalBranchName = worktreeNames.BranchName;
        string canonicalWorktreePath = isSharedWorktree
            ? _phaseOptions.SharedWorktreePath!
            : worktreeNames.WorktreePath;

        if (isRework)
        {
            // Rework always resolves by scanning git worktree list; the shared-worktree path
            // also appears in the list so this branch is correct for both cases.
            var existingWorktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
            bool reworkWorktreeFound = false;
            foreach (var w in existingWorktrees)
            {
                if (w.Branch == worktreeNames.BranchName)
                {
                    canonicalWorktreePath = w.Path;
                    reworkWorktreeFound = true;
                    break;
                }
                string wPathFull;
                try { wPathFull = Path.GetFullPath(w.Path); }
                catch { wPathFull = w.Path; }
                if (string.Equals(wPathFull, worktreeNames.WorktreePath, StringComparison.OrdinalIgnoreCase) ||
                    (isSharedWorktree && string.Equals(wPathFull, Path.GetFullPath(_phaseOptions.SharedWorktreePath!), StringComparison.OrdinalIgnoreCase)))
                {
                    canonicalWorktreePath = w.Path;
                    canonicalBranchName = string.IsNullOrEmpty(w.Branch) ? worktreeNames.BranchName : w.Branch;
                    reworkWorktreeFound = true;
                    break;
                }
                if (w.Branch.StartsWith(ticketBranchPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    canonicalWorktreePath = w.Path;
                    canonicalBranchName = w.Branch;
                    reworkWorktreeFound = true;
                    break;
                }
            }
            // Last-resort fallback: check if the computed path exists on disk.
            // Handles manually-created worktrees not registered with git, and test fakes
            // that return empty from ListWorktreesAsync but create the directory on disk.
            if (!reworkWorktreeFound && Directory.Exists(worktreeNames.WorktreePath))
                reworkWorktreeFound = true;
            if (!reworkWorktreeFound && isSharedWorktree && Directory.Exists(_phaseOptions.SharedWorktreePath!))
                reworkWorktreeFound = true;
            if (!reworkWorktreeFound)
            {
                var failureReason = $"rework expected existing worktree at {worktreeNames.WorktreePath} but it does not exist";
                EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, failureReason);
                return new ImplementResult(false, ticketId, null, worktreeNames.BranchName, worktreeNames.WorktreePath, failureReason);
            }
        }

        // Step 8: Build brief
        var brief = ImplementBriefBuilder.Build(_worker.Name, ticket, repoState, canonicalBranchName, canonicalWorktreePath, _project, _phaseOptions.ReviewFeedback);

        // Step 9: Set up the working directory for the ticket.
        // - Shared-worktree (initial): create the ticket branch inside the pre-existing worktree.
        // - Standalone (initial): create a new worktree with the ticket branch.
        // - Rework: reuses the existing worktree found above; no git operation needed.
        if (!isRework)
        {
            if (isSharedWorktree)
            {
                // Create the ticket's branch inside the chain's shared worktree.
                var branchResult = await _git.CreateBranchAsync(
                    canonicalBranchName,
                    baseRef,
                    canonicalWorktreePath,
                    ct).ConfigureAwait(false);
                if (!branchResult.Success)
                {
                    var failureReason = $"branch create in shared worktree failed: {branchResult.FailureReason}";
                    EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, failureReason);
                    return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath, failureReason);
                }
            }
            else
            {
                var createResult = await _git.CreateWorktreeAsync(
                    canonicalWorktreePath,
                    canonicalBranchName,
                    baseRef,
                    workingDirectory,
                    ct).ConfigureAwait(false);
                if (!createResult.Success)
                {
                    var failureReason = $"worktree create failed: {createResult.FailureReason}";
                    EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, failureReason);
                    return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath, failureReason);
                }
            }
        }

        // Step 9: Transition Ready -> InProgress (initial round only; rework starts already InProgress)
        if (!isRework)
        {
            await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
            {
                ["from"] = "Ready",
                ["to"] = "InProgress"
            }, ct).ConfigureAwait(false);
        }

        // Step 10: Emit WorkerSpawn
        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _options.WorkerName
        }, ct).ConfigureAwait(false);

        // Step 11: Execute worker inside the worktree
        var workerOptions = new WorkerOptions(_options.WorkerTimeout, _options.WorkerAllowedTools,
            DebugCaptureDirectory: _options.DebugCaptureDirectory,
            LiveStdoutSink: _options.LiveStdoutSink,
            LiveStderrSink: _options.LiveStderrSink,
            ProgressDigestSink: _options.ProgressDigestSink,
            Size: WorkerSizeMapper.FromTicketSize(ticket.Size));
        if (_options.DebugCaptureDirectory is not null)
            Directory.CreateDirectory(_options.DebugCaptureDirectory);
        var workerResult = await _worker.ExecuteAsync(brief, canonicalWorktreePath, workerOptions, ct).ConfigureAwait(false);

        // Step 12: Emit VerifierVerdict
        var verdictData = new Dictionary<string, object> { ["status"] = workerResult.Status.ToString() };
        var verdictReason = workerResult.FailureReason ?? (workerResult.Status != Status.Ok ? workerResult.Summary : null);
        if (verdictReason is not null) verdictData["failure_reason"] = verdictReason;
        await EmitAsync(EventKind.VerifierVerdict, ticketId, verdictData, ct).ConfigureAwait(false);

        // Step 13: LlmCall event if usage present
        if (workerResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = LlmUsageFlattener.Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
            }
        }

        // Step 14: If worker failed, leave in InProgress
        if (workerResult.Status != Status.Ok)
            return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                workerResult.FailureReason ?? workerResult.Summary ?? workerResult.Status.ToString(),
                EscalationWorkerResult: workerResult.Status == Status.Escalate ? workerResult : null);

        // Step 15: Extract commit_sha from metadata
        var metadataCommitSha = TryGetString(workerResult.Metadata, "commit_sha");
        if (string.IsNullOrEmpty(metadataCommitSha))
            return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                "worker metadata missing commit_sha");

        // Step 15b: Resolve IMPLEMENT_SUMMARY block if present
        string? implementSummaryMarkdown = null;
        if (workerResult.Blocks != null)
        {
            FencedBlockResolver.TryResolveRef(workerResult.Blocks, workerResult.Metadata, "summary_ref", out implementSummaryMarkdown, out _);
        }

        // Step 16: Verify against actual HEAD; prefer actual HEAD if it differs
        var actualHeadSha = await _git.HeadShaAsync(canonicalWorktreePath, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(actualHeadSha))
            actualHeadSha = metadataCommitSha;
        var discrepancyNote = (actualHeadSha != metadataCommitSha)
            ? $" (worker reported {metadataCommitSha}, HEAD is {actualHeadSha})"
            : "";

        // Step 17: Post implemented_at comment, including rendered summary if available
        var summaryHtml = implementSummaryMarkdown is not null
            ? MarkdownRenderer.Render(implementSummaryMarkdown)
            : "";
        var commentHtml = $"<p>[implemented_at: {actualHeadSha}] (branch {canonicalBranchName}){discrepancyNote}</p>{summaryHtml}";
        await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        // Step 18: Transition InProgress -> InReview
        await _ticketing.TransitionAsync(ticketId, TicketState.InReview, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = "InProgress",
            ["to"] = "InReview"
        }, ct).ConfigureAwait(false);

        // Step 19: Return success
        int reworkRound = _phaseOptions.ReviewFeedback?.ReworkRoundNumber ?? 0;
        return new ImplementResult(true, ticketId, actualHeadSha, canonicalBranchName, canonicalWorktreePath, null, reworkRound);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var implResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = implResult.Success
            ? new Dictionary<string, string>
            {
                ["commit_sha"] = implResult.CommitSha!,
                ["branch"] = implResult.BranchName!,
                ["worktree_path"] = implResult.WorktreePath!
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(implResult.Success, implResult.TicketId, Phase.Implement, implResult.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Implement,
            data), ct).ConfigureAwait(false);
    }


    // The initial implement round requires a Ready ticket. When the ticket is in
    // some other state, the right next move depends on where it already sits in the
    // lifecycle, so surface state-specific guidance instead of one generic hint.
    // (A blanket "did you mean to invoke rework?" is misleading from InReview, where
    // rework would also fail - rework requires InProgress.)
    private static string InitialRoundStateGuidance(string ticketId, TicketState state) => state switch
    {
        TicketState.InReview =>
            $"initial round invoked but {ticketId} is in InReview - it has already been implemented and is awaiting review. Run `build review {ticketId}` (or `build ship {ticketId}` if review has already passed).",
        TicketState.InProgress =>
            $"initial round invoked but {ticketId} is in InProgress - run `build rework {ticketId}` to apply review feedback, or reset it to Ready to start the implementation over.",
        TicketState.Backlog =>
            $"initial round invoked but {ticketId} is in Backlog, not Ready - run `build plan {ticketId}` to plan and approve it first.",
        TicketState.Planning =>
            $"initial round invoked but {ticketId} is stuck in Planning, not Ready - its plan phase did not finish; reset it to Backlog and re-run `build plan {ticketId}` before implementing.",
        TicketState.Done =>
            $"initial round invoked but {ticketId} is Done - nothing to implement.",
        TicketState.Cancelled =>
            $"initial round invoked but {ticketId} is Cancelled - nothing to implement.",
        _ =>
            $"initial round invoked but {ticketId} is in {state}, not Ready.",
    };

    private static string? TryGetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }
}
