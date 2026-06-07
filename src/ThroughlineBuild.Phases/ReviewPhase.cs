using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Verification;
using static ThroughlineBuild.Helpers.LlmUsageFlattener;

namespace ThroughlineBuild.Phases;

public record ReviewOptions(
    IReadOnlyList<CheckSpec> Checks,
    WorkerOptions VerifierWorkerOptions);

public record ReviewResult(
    bool Success,
    string TicketId,
    VerdictKind? Verdict,
    string? VerdictRationale,
    IReadOnlyList<string> ChecksFailed,
    string? FailureReason);

public class ReviewPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _verifierWorker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly ReviewOptions _reviewOptions;
    private readonly IGitClient _git;
    private readonly IVerifier? _verifierOverride;
    private readonly AutomatedChecksRunner? _checksRunner;
    private readonly ProjectContext _project;

    public ReviewPhase(
        ITicketing ticketing,
        IWorkerAgent verifierWorker,
        IEventSink events,
        BuildOptions options,
        ReviewOptions reviewOptions,
        IGitClient? gitClient = null,
        IVerifier? verifierOverride = null,
        AutomatedChecksRunner? checksRunner = null,
        ProjectContext? project = null)
    {
        _ticketing = ticketing;
        _verifierWorker = verifierWorker;
        _events = events;
        _options = options;
        _reviewOptions = reviewOptions;
        _git = gitClient ?? new ProcessGitClient();
        _verifierOverride = verifierOverride;
        _checksRunner = checksRunner;
        _project = project ?? ProjectContext.Empty;
    }

    public Phase Phase => Phase.Review;

    public async Task<ReviewResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Fetch ticket
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Parent-ticket aggregate review path
        var reviewChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (reviewChildren.Count > 0)
        {
            return await RunParentReviewAsync(ticketId, ticket, reviewChildren, ct).ConfigureAwait(false);
        }

        // Step 2: Validate state
        if (ticket.State != TicketState.InReview)
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                "ticket not in InReview state");

        // Step 3: Compute and locate worktree
        var worktreeNames = PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, workingDirectory);
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        bool worktreeFound = false;
        string canonicalWorktreePath = worktreeNames.WorktreePath;
        string canonicalBranchName = worktreeNames.BranchName;
        var ticketBranchName = worktreeNames.BranchName;
        foreach (var w in worktrees)
        {
            if (w.Branch == worktreeNames.BranchName)
            {
                canonicalWorktreePath = w.Path;
                worktreeFound = true;
                break;
            }
            string wPathFull;
            try { wPathFull = Path.GetFullPath(w.Path); }
            catch { wPathFull = w.Path; }
            if (string.Equals(wPathFull, worktreeNames.WorktreePath, StringComparison.OrdinalIgnoreCase))
            {
                canonicalWorktreePath = w.Path;
                canonicalBranchName = string.IsNullOrEmpty(w.Branch) ? worktreeNames.BranchName : w.Branch;
                worktreeFound = true;
                break;
            }
            if (PhaseWorktreeLayout.IsTicketBranch(w.Branch, ticketBranchName))
            {
                canonicalWorktreePath = w.Path;
                canonicalBranchName = w.Branch;
                worktreeFound = true;
                break;
            }
        }
        if (!worktreeFound)
        {
            // Recovery: the ticket is InReview and its branch may still exist, but the worktree was
            // torn down - e.g. a parent chain removed its shared worktree at chain end, or a prior
            // run was interrupted. Recreate a worktree checked out on the ticket branch so review can
            // run, instead of dead-ending at "worktree not found" with no way to resume.
            var branches = await _git.ListLocalBranchesAsync(canonicalBranchName, workingDirectory, ct).ConfigureAwait(false);
            if (branches.Any(b => string.Equals(b, canonicalBranchName, StringComparison.Ordinal)))
            {
                var recovered = await _git.CheckoutWorktreeAsync(canonicalWorktreePath, canonicalBranchName, workingDirectory, ct).ConfigureAwait(false);
                if (recovered.Success)
                {
                    canonicalWorktreePath = recovered.AbsolutePath ?? canonicalWorktreePath;
                    worktreeFound = true;
                    Console.Error.WriteLine(
                        $"[{ticket.Id}] review: feature worktree was missing; reconstructed from branch " +
                        $"{canonicalBranchName} at {canonicalWorktreePath}");
                }
            }
        }
        if (!worktreeFound)
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                $"feature worktree not found at {worktreeNames.WorktreePath}");

        // Step 4: Resolve base ref (origin/main with fallback to local main) and its SHA
        string baseRef;
        string mainSha;
        try
        {
            (baseRef, mainSha) = await BaseRefResolver.ResolveAsync(_git, workingDirectory, _options.TargetBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                $"git rev-parse failed: {ex.Message}");
        }

        // Step 5: Build RepoState and implementer brief
        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);
        var implementerBrief = ImplementBriefBuilder.Build(_verifierWorker.Name, ticket, repoState, canonicalBranchName, canonicalWorktreePath, _project);

        // Step 6a: Determine the implementer commit under review. The freshest [implemented_at:
        // <sha>] marker proves implement ran and self-reports a SHA - selecting it by comment
        // creation time (not list position) avoids reading a stale prior-run marker (TLB-412).
        // But the worktree branch HEAD is ground truth: an implementer that amends or squashes
        // AFTER posting the marker leaves it pointing at a now-superseded commit, while the
        // automated checks (Step 7) and the diff (Step 6b) run against HEAD. When the two
        // diverge, attribute the review to HEAD and surface the drift, so the verifier never
        // reasons about an orphaned commit that the live checks did not run against (TLB-414).
        var comments = await _ticketing.GetCommentsAsync(ticketId, ct).ConfigureAwait(false);
        var markerSha = CommentMarkers.LatestValue(comments, "implemented_at");
        if (string.IsNullOrEmpty(markerSha))
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                "no implemented_at marker found - ticket reached InReview without an implement marker, ReviewPhase cannot reconstruct implementer state");

        string? headSha = null;
        try { headSha = await _git.HeadShaAsync(canonicalWorktreePath, ct).ConfigureAwait(false); }
        catch { /* best-effort: HEAD unavailable, fall back to the marker below */ }

        var implementerCommitSha = markerSha;
        if (!string.IsNullOrEmpty(headSha) && !string.Equals(headSha, markerSha, StringComparison.Ordinal))
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "implemented_at_superseded",
                ["marker_sha"] = markerSha,
                ["head_sha"] = headSha!
            }, ct).ConfigureAwait(false);
            implementerCommitSha = headSha!;
        }

        // Step 6b: Compute diff and synthesize implementer WorkerResult
        var diff = await _git.DiffAsync(baseRef, canonicalBranchName, workingDirectory, includePatchContent: true, ct).ConfigureAwait(false);
        var implementerResult = new WorkerResult(
            Status.Ok,
            $"Reconstructed from implemented_at: {implementerCommitSha} ({diff.Entries.Count} files changed)",
            diff.Entries.Select(e => e.Path).ToList(),
            null,
            new Dictionary<string, object>
            {
                ["commit_sha"] = implementerCommitSha
            });

        // Step 7: Run automated checks (failures do not short-circuit; verifier sees them)
        var runner = _checksRunner ?? new AutomatedChecksRunner();
        var checkResults = await runner.RunAsync(_reviewOptions.Checks, canonicalWorktreePath, ct).ConfigureAwait(false);

        // Step 8: Construct verifier - must run in the feature worktree, not the main working directory,
        // so the worker cannot dirty tracked files in main and block the subsequent ship pre-flight check.
        var effectiveVerifierOptions = _reviewOptions.VerifierWorkerOptions with
        {
            Size = WorkerSizeMapper.FromTicketSize(ticket.Size)
        };
        var verifier = _verifierOverride
            ?? new WorkerAgentReviewer(_verifierWorker, ticket, checkResults, effectiveVerifierOptions, canonicalWorktreePath, _project);

        // Step 9: Emit WorkerSpawn (role = verifier)
        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _verifierWorker.Name,
            ["role"] = "verifier"
        }, ct).ConfigureAwait(false);

        // Step 9b: Snapshot the repo-global git state the verifier must not mutate. codex and
        // gemini ignore verifier_allowed_tools and run unsandboxed (read-only enforcement is not
        // available on Windows - see TLB-478), so this before/after delta is the only backstop
        // against a verifier that runs git stash or moves HEAD. The stash stack is shared across
        // worktrees (a leaked stash corrupts a later ticket); a HEAD move corrupts what ships.
        // Step 10b catches uncommitted writes, but a stash or reset leaves a CLEAN tree and would
        // slip past it.
        var stashCountBeforeReview = (await _git.ListStashEntriesAsync(canonicalWorktreePath, ct).ConfigureAwait(false)).Count;
        string? headShaBeforeReview = await TryHeadShaAsync(canonicalWorktreePath, ct).ConfigureAwait(false);

        // Step 10: Run verifier
        var verdict = await verifier.VerifyAsync(implementerBrief, diff, implementerResult, ct).ConfigureAwait(false);

        // Step 10b: Dirty-worktree check after verifier exit - hard-fail, no retry
        var reviewDirtyPaths = await WorkingTreeHygieneGate.DirtyFilesCheckAsync(_git, canonicalWorktreePath, ct).ConfigureAwait(false);
        if (reviewDirtyPaths.Count > 0)
        {
            // Emit VerifierVerdict first so the operator can see what verdict the verifier produced
            await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
            {
                ["kind"] = verdict.Kind.ToString(),
                ["checks_failed_count"] = verdict.ChecksFailed.Count,
                ["rationale"] = verdict.Rationale,
                ["checks_failed"] = verdict.ChecksFailed
            }, ct).ConfigureAwait(false);
            var dirtyReason = FormatDirtyWorktreeReason("Review", reviewDirtyPaths);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "dirty_worktree_after_review",
                ["dirty_paths"] = reviewDirtyPaths
            }, ct).ConfigureAwait(false);
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(), dirtyReason);
        }

        // Step 10c: Shared-git-state guard. The verifier must not touch the stash stack or move
        // HEAD; on violation, drop any stash entries it pushed and hard-fail so mutated shared
        // state never reaches ship. See TLB-478 and the Step 9b snapshot.
        var stashCountAfterReview = (await _git.ListStashEntriesAsync(canonicalWorktreePath, ct).ConfigureAwait(false)).Count;
        string? headShaAfterReview = await TryHeadShaAsync(canonicalWorktreePath, ct).ConfigureAwait(false);
        int stashDelta = stashCountAfterReview - stashCountBeforeReview;
        bool headMoved = headShaBeforeReview is not null && headShaAfterReview is not null
            && !string.Equals(headShaBeforeReview, headShaAfterReview, StringComparison.Ordinal);
        if (stashDelta != 0 || headMoved)
        {
            // Surface the verdict the verifier produced before failing on the guard.
            await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
            {
                ["kind"] = verdict.Kind.ToString(),
                ["checks_failed_count"] = verdict.ChecksFailed.Count,
                ["rationale"] = verdict.Rationale,
                ["checks_failed"] = verdict.ChecksFailed
            }, ct).ConfigureAwait(false);

            // Drop the stash entries the verifier pushed. Dispatch is serial, so the top
            // stashDelta entries are exactly the verifier's. Best-effort.
            int stashDropped = 0;
            for (int i = 0; i < stashDelta; i++)
            {
                var drop = await _git.StashDropAsync("stash@{0}", canonicalWorktreePath, ct).ConfigureAwait(false);
                if (!drop.Success) break;
                stashDropped++;
            }

            var guardReason = FormatSharedStateReason(headMoved, headShaBeforeReview, headShaAfterReview, stashDelta, stashDropped);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "shared_git_state_mutated_after_review",
                ["head_before"] = headShaBeforeReview ?? "",
                ["head_after"] = headShaAfterReview ?? "",
                ["stash_delta"] = stashDelta,
                ["stash_dropped"] = stashDropped
            }, ct).ConfigureAwait(false);
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(), guardReason);
        }

        // Step 11: Emit VerifierVerdict
        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["kind"] = verdict.Kind.ToString(),
            ["checks_failed_count"] = verdict.ChecksFailed.Count,
            ["rationale"] = verdict.Rationale,
            ["checks_failed"] = verdict.ChecksFailed
        }, ct).ConfigureAwait(false);

        // Step 12: LlmCall event if verifier worker reported usage
        if (_verifierOverride is null && verifier is WorkerAgentReviewer ccr && ccr.LastWorkerResult is { } verifierResult && verifierResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
            }
        }

        // Step 13: Apply verdict
        string commentHtml;
        if (verdict.Kind == VerdictKind.Pass)
        {
            commentHtml = $"<p><strong>reviewed:</strong> pass - {verdict.Rationale}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
        }
        else if (verdict.Kind == VerdictKind.Rework)
        {
            var checksList = verdict.ChecksFailed.Count > 0
                ? "<br/>checks_failed: " + string.Join(", ", verdict.ChecksFailed)
                : "";
            commentHtml = $"<p><strong>reviewed:</strong> rework - {verdict.Rationale}{checksList}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
            await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
            {
                ["from"] = "InReview",
                ["to"] = "InProgress"
            }, ct).ConfigureAwait(false);
        }
        else // Fail
        {
            var checksList = verdict.ChecksFailed.Count > 0
                ? "<br/>checks_failed: " + string.Join(", ", verdict.ChecksFailed)
                : "";
            commentHtml = $"<p><strong>reviewed:</strong> fail - {verdict.Rationale}{checksList}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
        }

        // Step 14: Return success
        return new ReviewResult(true, ticketId, verdict.Kind, verdict.Rationale, verdict.ChecksFailed, null);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var reviewResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = reviewResult.Success
            ? new Dictionary<string, string>
            {
                ["verdict"] = reviewResult.Verdict?.ToString() ?? "",
                ["rationale"] = reviewResult.VerdictRationale ?? "",
                ["checks_failed_count"] = reviewResult.ChecksFailed.Count.ToString()
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(reviewResult.Success, reviewResult.TicketId, Phase.Review, reviewResult.FailureReason, outputs);
    }

    private async Task<ReviewResult> RunParentReviewAsync(
        string ticketId,
        Ticket ticket,
        IReadOnlyList<Ticket> children,
        CancellationToken ct)
    {
        // Classify children by state
        var inFlight = children.Where(c => c.State == TicketState.InProgress || c.State == TicketState.InReview).ToList();
        var notDone = children.Where(c => c.State != TicketState.Done).ToList();
        var allDone = notDone.Count == 0;

        VerdictKind kind;
        string rationale;
        string commentHtml;

        if (inFlight.Count > 0)
        {
            // Any child InProgress or InReview -> Rework
            kind = VerdictKind.Rework;
            var blockerIds = string.Join(", ", inFlight.Select(c => c.Id));
            rationale = $"children still in progress: {blockerIds}";
            commentHtml = $"<p><strong>reviewed:</strong> rework - {rationale}</p>";
        }
        else if (allDone)
        {
            // All Done -> Pass
            kind = VerdictKind.Pass;
            rationale = $"all {children.Count} children are Done";
            commentHtml = $"<p><strong>reviewed:</strong> pass - {rationale}</p>";
        }
        else
        {
            // Some children not Done and not in-flight -> Fail
            kind = VerdictKind.Fail;
            var blockerIds = string.Join(", ", notDone.Select(c => c.Id));
            rationale = $"children not Done: {blockerIds}";
            commentHtml = $"<p><strong>reviewed:</strong> fail - {rationale}</p>";
        }

        await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        if (kind == VerdictKind.Rework)
        {
            await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
            {
                ["from"] = ticket.State.ToString(),
                ["to"] = "InProgress"
            }, ct).ConfigureAwait(false);
        }

        return new ReviewResult(true, ticketId, kind, rationale, Array.Empty<string>(), null);
    }

    // Best-effort HEAD read: the shared-state guard treats an unreadable HEAD as "unknown"
    // and skips the comparison rather than false-failing the review.
    private async Task<string?> TryHeadShaAsync(string worktreePath, CancellationToken ct)
    {
        try { return await _git.HeadShaAsync(worktreePath, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static string FormatSharedStateReason(bool headMoved, string? headBefore, string? headAfter, int stashDelta, int stashDropped)
    {
        var parts = new List<string>();
        if (headMoved)
            parts.Add($"feature branch HEAD moved during review ({headBefore} -> {headAfter})");
        if (stashDelta > 0)
            parts.Add($"verifier pushed {stashDelta} stash entr{(stashDelta == 1 ? "y" : "ies")} onto the repo-global stack (dropped {stashDropped})");
        else if (stashDelta < 0)
            parts.Add($"verifier consumed {-stashDelta} pre-existing stash entr{(stashDelta == -1 ? "y" : "ies")}");
        return "Review: verifier mutated shared git state - " + string.Join("; ", parts);
    }

    private static string FormatDirtyWorktreeReason(string phase, IReadOnlyList<string> dirtyPaths, int sampleLimit = 5)
    {
        var sample = dirtyPaths.Take(sampleLimit).ToList();
        var sampleStr = string.Join(", ", sample);
        var morePart = dirtyPaths.Count > sampleLimit ? $"; ... and {dirtyPaths.Count - sampleLimit} more" : "";
        return $"{phase}: worktree dirty after worker exit - {dirtyPaths.Count} file(s) uncommitted: {sampleStr}{morePart}";
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Review,
            data), ct).ConfigureAwait(false);
    }

}
