using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

public record ImplementPhaseOptions(
    ReviewFeedback? ReviewFeedback = null,
    string? SharedWorktreePath = null,
    ChainCommitRange? ChainCommitRange = null);

public record ImplementResult(
    bool Success,
    string TicketId,
    string? CommitSha,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason,
    int ReworkRoundNumber = 0,
    WorkerResult? EscalationWorkerResult = null,
    CompletionClaim? CompletionClaim = null,
    long? LlmInputTokens = null,
    long? LlmOutputTokens = null);

public class ImplementPhase : IWorkflowPhase
{
    private static readonly Regex ImplementedAtParagraphPattern = new(
        @"<p\b[^>]*>\s*\[implemented_at:[\s\S]*?</p>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

    // Compile-time gate for the chain-handoff pointer feature (Plan C, Brief 10).
    // When false, the implement brief is identical to the pre-Plan-C baseline: no
    // touched-files in RelevantFiles and no chain_pointer in Context. The derivation
    // in ChainPhase still runs (it is cheap and harmless), but the result is discarded
    // here at the consumption side where the brief-token cost actually lands.
    // Set to false to opt out; rebuild to apply the change.
    private const bool HandoffPointerEnabled = true;

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

    /// <summary>
    /// A worktree-confined file reader for <see cref="PreloadedContextBuilder"/>: resolves a relative
    /// path under <paramref name="worktreeRoot"/> and returns its text, or null if the file is absent,
    /// unreadable, or resolves OUTSIDE the worktree (defense in depth - the builder already rejects
    /// rooted/`..` paths, this catches anything that slips through). Never throws.
    /// </summary>
    internal static Func<string, string?> MakeWorktreeReader(string worktreeRoot)
    {
        string root;
        try { root = Path.GetFullPath(worktreeRoot); }
        catch { return static _ => null; }

        return relPath =>
        {
            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, relPath));
                bool inside = string.Equals(combined, root, StringComparison.OrdinalIgnoreCase)
                    || combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!inside)
                    return null;
                return File.Exists(combined) ? File.ReadAllText(combined) : null;
            }
            catch
            {
                return null;
            }
        };
    }

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
        var ticketBranchName = PhaseWorktreeLayout.BranchName(ticket.Id);
        var hygieneFailure = await WorkingTreeHygieneGate.CheckAsync(_git, workingDirectory, ticketBranchName, ct).ConfigureAwait(false);
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

        // Step 3: Resolve base ref and its SHA. BaseRefResolver prefers origin/<target> but
        // advances to the local <target> tip when it is strictly ahead (the accumulating state
        // a local-shipping chain leaves behind), so each chain child branches from its shipped
        // siblings instead of the frozen origin (TLB-411).
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

        // Step 5: Drift check - compare the freshest [planned_at: <sha>] marker against the base.
        // Selecting by comment creation time (not list position) keeps the check on the current
        // run's marker; a chain re-run also carries planned_at markers from prior runs (TLB-412).
        var comments = await _ticketing.GetCommentsAsync(ticketId, ct).ConfigureAwait(false);
        var plannedAtSha = CommentMarkers.LatestValue(comments, "planned_at");
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
                if (PhaseWorktreeLayout.IsTicketBranch(w.Branch, ticketBranchName))
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
            // Recovery (TLB-438): a stopped parent chain tears down the shared worktree at chain
            // end, but the ticket branch and its commits survive. Rather than dead-ending the
            // resume, recreate a worktree checked out to the existing branch at the canonical path.
            // Covers both `build chain <child>` and `build rework <child>`, which share this path.
            // Only fall through to failure if the branch is gone too or the worktree add fails.
            if (!reworkWorktreeFound)
            {
                var recovery = await _git.CheckoutWorktreeAsync(canonicalWorktreePath, canonicalBranchName, workingDirectory, ct).ConfigureAwait(false);
                if (recovery.Success)
                {
                    if (!string.IsNullOrEmpty(recovery.AbsolutePath))
                        canonicalWorktreePath = recovery.AbsolutePath;
                    reworkWorktreeFound = true;
                }
                else
                {
                    var failureReason = $"rework expected existing worktree at {worktreeNames.WorktreePath} but it does not exist, and could not recreate it from branch {canonicalBranchName}: {recovery.FailureReason}";
                    EarlyExitManifest.Write(_options.DebugCaptureDirectory, Phase.Implement.ToString(), ticketId, failureReason);
                    return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath, failureReason);
                }
            }
        }

        // Step 8: Build brief.
        // When HandoffPointerEnabled is false the range is suppressed at the consumption side
        // so the brief is byte-identical to the pre-Plan-C baseline. The derivation in
        // ChainPhase still ran (it is cheap and harmless); we simply discard the result here.
        var effectiveChainRange = HandoffPointerEnabled ? _phaseOptions.ChainCommitRange : null;
        var reworkContext = isRework
            ? await BuildReworkBriefContextAsync(comments, baseRef, canonicalBranchName, workingDirectory, ct).ConfigureAwait(false)
            : null;
        // NOTE: the pre-load section + brief build moved BELOW Step 9 (experiment 3, Defect 2). The
        // reader must see the MATERIALIZED worktree (baseRef + prior briefs' commits in the chain path),
        // which only exists after Step 9 checks the ticket branch out. Nothing between here and the
        // worker spawn reads `brief`, so the build is safe to defer to Step 9b.

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

        // Step 9b (experiment 3): build the pre-load section against the NOW-materialized worktree and
        // emit loud, countable telemetry, then build the brief. The reader sees baseRef + prior briefs'
        // commits (chain), the freshly-created worktree (standalone), or the existing one (rework) - all
        // three correct. The events are advisory: BuildAndReportPreloadAsync emits and we PROCEED.
        var preloadedContextSection = await BuildAndReportPreloadAsync(
            ticketId, ticket.DescriptionHtml, MakeWorktreeReader(canonicalWorktreePath), ct).ConfigureAwait(false);
        var brief = ImplementBriefBuilder.Build(_worker.Name, ticket, repoState, canonicalBranchName, canonicalWorktreePath, _project, _phaseOptions.ReviewFeedback, effectiveChainRange, reworkContext, preloadedContextSection);

        // Step 10: Emit WorkerSpawn
        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _options.WorkerName
        }, ct).ConfigureAwait(false);

        // Step 11: Execute worker inside the worktree
        // Experiment 4 (L2b): lean planning is gated on the opt-in flag AND S-effort only. Never M or L
        // (complex briefs legitimately need to plan; stripping their tools backfires). The brief's L2a
        // prompt line is gated on the same predicate inside ImplementBriefBuilder.
        var lean = _project.ContextHygiene && ticket.Size == Size.S;
        var workerOptions = new WorkerOptions(_options.WorkerTimeout, _options.WorkerAllowedTools,
            DebugCaptureDirectory: _options.DebugCaptureDirectory,
            LiveStdoutSink: _options.LiveStdoutSink,
            LiveStderrSink: _options.LiveStderrSink,
            ProgressDigestSink: _options.ProgressDigestSink,
            Size: WorkerSizeMapper.FromTicketSize(ticket.Size),
            LeanPlanning: lean,
            DebugTranscript: new DebugTranscriptContext(
                BuildVersion: _options.BuildVersion,
                SessionId: _options.SessionId,
                ReworkRound: _phaseOptions.ReviewFeedback?.ReworkRoundNumber));
        if (_options.DebugCaptureDirectory is not null)
            Directory.CreateDirectory(_options.DebugCaptureDirectory);
        var workerResult = await _worker.ExecuteAsync(brief, canonicalWorktreePath, workerOptions, ct).ConfigureAwait(false);

        // Step 12: Emit VerifierVerdict
        var verdictData = new Dictionary<string, object> { ["status"] = workerResult.Status.ToString() };
        var verdictReason = workerResult.FailureReason ?? (workerResult.Status != Status.Ok ? workerResult.Summary : null);
        if (verdictReason is not null) verdictData["failure_reason"] = verdictReason;
        await EmitAsync(EventKind.VerifierVerdict, ticketId, verdictData, ct).ConfigureAwait(false);

        // Step 13: LlmCall event if usage present; capture token counts for cost ledger.
        long? llmInputTokens = null;
        long? llmOutputTokens = null;
        if (workerResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = LlmUsageFlattener.Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
                llmInputTokens = TryGetLong(llmData, "input_tokens");
                llmOutputTokens = TryGetLong(llmData, "output_tokens");
            }
        }

        // Step 13b (experiment 4): advisory per-turn context-attribution telemetry. The claude-code
        // worker stashes a flat per-turn series on Metadata["context_turns"]; re-emit it as a
        // CostLedger event so the intra-session cache_read ramp is derivable straight from the event
        // log (mirrors the preload_summary advisory: emit and proceed). Stack-agnostic: this reads
        // generic dictionary values by key and names no tool or stack. Runs for every brief,
        // independent of effort or any flag.
        if (workerResult.Metadata.TryGetValue("context_turns", out var ctxTurnsObj)
            && ctxTurnsObj is IReadOnlyDictionary<string, object> ctxTurns)
        {
            var attribution = new Dictionary<string, object>
            {
                ["kind"] = "context_attribution",
                ["attribution_note"] = "cache_creation lags tool_use ~1 turn; per-class split is approximate",
            };
            foreach (var key in new[]
            {
                "turns", "total_cache_read", "slope_ratio",
                "cache_read_series", "cache_creation_series", "output_series",
                "read_bytes", "write_bytes", "todo_bytes", "task_bytes", "bash_bytes", "other_bytes",
            })
            {
                if (ctxTurns.TryGetValue(key, out var v) && v is not null)
                    attribution[key] = v;
            }
            await EmitAsync(EventKind.CostLedger, ticketId, attribution, ct).ConfigureAwait(false);
        }

        // Step 14: If worker failed, leave in InProgress - UNLESS the worker ran a clean
        // session that committed real work but failed to return a usable WORKER_RESULT envelope.
        // Two narrow, recoverable cases (both gated on clean worktree + HEAD advanced past base):
        //   - envelope_status=missing: no trailing marker at all (TLB-471).
        //   - envelope_status=missing_status: a marker whose valid-JSON payload omitted the
        //     required 'status' field (TLB-476).
        // ImplementPhase already trusts git HEAD as ground truth (Step 16), so discarding a
        // committed, reviewable branch over a missing/garbled trailing envelope just throws the
        // work away. Synthesize the success the worker failed to report and let review be the gate.
        bool recoveredEnvelope = false;
        if (workerResult.Status != Status.Ok)
        {
            var salvaged = await TrySalvageCommittedSessionAsync(workerResult, canonicalWorktreePath, mainSha, ct).ConfigureAwait(false);
            if (salvaged is null)
                return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                    workerResult.FailureReason ?? workerResult.Summary ?? workerResult.Status.ToString(),
                    EscalationWorkerResult: workerResult.Status == Status.Escalate ? workerResult : null);

            recoveredEnvelope = true;
            workerResult = salvaged;
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "implement_envelope_recovered",
                ["commit_sha"] = TryGetString(workerResult.Metadata, "commit_sha") ?? ""
            }, ct).ConfigureAwait(false);
        }

        // Step 14b: Dirty-worktree check after worker exit - one bounded retry
        var dirtyPaths = await WorkingTreeHygieneGate.DirtyFilesCheckAsync(_git, canonicalWorktreePath, ct).ConfigureAwait(false);
        if (dirtyPaths.Count > 0)
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "dirty_worktree_first_attempt",
                ["dirty_paths"] = dirtyPaths
            }, ct).ConfigureAwait(false);

            var samplePaths = dirtyPaths.Take(5).ToList();
            var sampleStr = string.Join(", ", samplePaths);
            var moreNote = dirtyPaths.Count > 5 ? $" (and {dirtyPaths.Count - 5} more)" : "";
            var retryInstruction = brief.Instruction + $"\n\n[Dirty-worktree note: the previous attempt left {dirtyPaths.Count} uncommitted file(s): {sampleStr}{moreNote}. Commit all changes before returning WORKER_RESULT.]";
            var retryBrief = brief with { Instruction = retryInstruction };

            var retryWorkerResult = await _worker.ExecuteAsync(retryBrief, canonicalWorktreePath, workerOptions, ct).ConfigureAwait(false);

            var retryVerdictData = new Dictionary<string, object> { ["status"] = retryWorkerResult.Status.ToString() };
            var retryVerdictReason = retryWorkerResult.FailureReason ?? (retryWorkerResult.Status != Status.Ok ? retryWorkerResult.Summary : null);
            if (retryVerdictReason is not null) retryVerdictData["failure_reason"] = retryVerdictReason;
            await EmitAsync(EventKind.VerifierVerdict, ticketId, retryVerdictData, ct).ConfigureAwait(false);

            if (retryWorkerResult.Status != Status.Ok)
                return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                    retryWorkerResult.FailureReason ?? retryWorkerResult.Summary ?? retryWorkerResult.Status.ToString(),
                    EscalationWorkerResult: retryWorkerResult.Status == Status.Escalate ? retryWorkerResult : null);

            var retryDirtyPaths = await WorkingTreeHygieneGate.DirtyFilesCheckAsync(_git, canonicalWorktreePath, ct).ConfigureAwait(false);
            if (retryDirtyPaths.Count > 0)
            {
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "dirty_worktree_retry_failed",
                    ["dirty_paths"] = retryDirtyPaths
                }, ct).ConfigureAwait(false);
                return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                    FormatDirtyWorktreeReason("Implement", retryDirtyPaths));
            }

            workerResult = retryWorkerResult;
        }

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

        // Step 15c: Resolve COMPLETION_CLAIM if worker opted in via completion_claim_ref metadata key.
        // Workers that omit this key are treated as pre-claim format (null claim, proceed normally).
        // Workers that include the key but produce an invalid or missing block get one targeted re-ask
        // before a hard failure, keeping the anti-lazy-worker property without burning a rework round.
        CompletionClaim? completionClaim = null;
        if (workerResult.Metadata.ContainsKey("completion_claim_ref"))
        {
            if (!TryExtractClaim(workerResult.Metadata, workerResult.Blocks, out completionClaim, out var claimError))
            {
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "claim_invalid_first_attempt",
                    ["detail"] = claimError ?? "unknown"
                }, ct).ConfigureAwait(false);

                var reAskInstruction = BuildClaimReAskInstruction(ticketId, canonicalBranchName, metadataCommitSha);
                var reAskBrief = brief with { Instruction = reAskInstruction };
                var reAskResult = await _worker.ExecuteAsync(reAskBrief, canonicalWorktreePath, workerOptions, ct).ConfigureAwait(false);

                if (!TryExtractClaim(reAskResult.Metadata, reAskResult.Blocks, out completionClaim, out var reAskError))
                {
                    return new ImplementResult(false, ticketId, null, canonicalBranchName, canonicalWorktreePath,
                        $"worker failed to emit a valid COMPLETION_CLAIM after re-ask: {reAskError}");
                }
            }
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
        // Parens (not brackets) so the marker parser does not read this note as a second marker.
        var recoveryNote = "";
        if (recoveredEnvelope)
        {
            var recoveredKind = TryGetString(workerResult.Metadata, WorkerResultMetadata.EnvelopeStatusKey);
            recoveryNote = recoveredKind == WorkerResultMetadata.EnvelopeMissingStatus
                ? " (recovered: worker emitted a WORKER_RESULT envelope missing the required status field; SHA reconstructed from git HEAD)"
                : " (recovered: worker omitted the WORKER_RESULT envelope; SHA reconstructed from git HEAD)";
        }
        var commentHtml = $"<p>[implemented_at: {actualHeadSha}] (branch {canonicalBranchName}){discrepancyNote}{recoveryNote}</p>{summaryHtml}";
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
        return new ImplementResult(true, ticketId, actualHeadSha, canonicalBranchName, canonicalWorktreePath, null, reworkRound,
            CompletionClaim: completionClaim, LlmInputTokens: llmInputTokens, LlmOutputTokens: llmOutputTokens);
    }

    // Salvage path for a worker that finished a clean, committed session but did not return a
    // usable WORKER_RESULT envelope. Two recoverable shapes, both tagged via envelope_status:
    //   - EnvelopeMissing ("missing"): no WORKER_RESULT marker at all (e.g. a long or
    //     context-compacted session that did the work, committed it, then ended its turn without
    //     the trailing marker). See TLB-471.
    //   - EnvelopeMissingStatus ("missing_status"): a marker was present and its payload parsed
    //     as a valid JSON object, but it omitted the required 'status' field (e.g. the worker
    //     hand-rolled a non-conforming summary like {"outcome":"Completed",...} instead of the
    //     prescribed {"status":...} schema). Same recoverable situation - arguably stronger,
    //     since the worker reported a real commit. See TLB-476.
    // Returns a synthesized Ok result - with commit_sha set to the real HEAD - when ALL of these
    // hold, else null so the caller fails exactly as before:
    //   - envelope_status is one of the two recoverable values above, NOT an explicit
    //     Failed/NeedsRework/Escalate envelope, a JSON syntax error, an unrecognized status enum
    //     value, a non-zero exit, or a timeout (those all mean the tree is not trustworthy);
    //   - the worktree is clean (uncommitted changes mean the session did not finish, so the
    //     work is not safely reviewable);
    //   - HEAD has advanced past the base ref (otherwise nothing was committed to review).
    // Review remains the real quality gate for whatever was committed.
    private async Task<WorkerResult?> TrySalvageCommittedSessionAsync(
        WorkerResult workerResult, string worktreePath, string baseSha, CancellationToken ct)
    {
        var envelopeStatus = TryGetString(workerResult.Metadata, WorkerResultMetadata.EnvelopeStatusKey);
        bool missingEnvelope = string.Equals(envelopeStatus, WorkerResultMetadata.EnvelopeMissing, StringComparison.Ordinal);
        bool missingStatus = string.Equals(envelopeStatus, WorkerResultMetadata.EnvelopeMissingStatus, StringComparison.Ordinal);
        if (!missingEnvelope && !missingStatus)
            return null;

        var dirty = await WorkingTreeHygieneGate.DirtyFilesCheckAsync(_git, worktreePath, ct).ConfigureAwait(false);
        if (dirty.Count > 0)
            return null;

        var headSha = await _git.HeadShaAsync(worktreePath, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(headSha) || string.Equals(headSha, baseSha, StringComparison.Ordinal))
            return null;

        var meta = new Dictionary<string, object>(workerResult.Metadata)
        {
            ["commit_sha"] = headSha
        };
        var summary = missingStatus
            ? "Recovered implement session (worker emitted a WORKER_RESULT envelope missing the required status field)"
            : "Recovered implement session (worker omitted WORKER_RESULT envelope)";
        return workerResult with
        {
            Status = Status.Ok,
            Summary = summary,
            FailureReason = null,
            Metadata = meta
        };
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

    /// <summary>
    /// Build the pre-load section from the worker's MATERIALIZED worktree (call AFTER Step 9 checks out
    /// baseRef - experiment 3, Defect 2) and emit loud, countable telemetry (Defect 3): a per-ticket
    /// preload_summary; a preload_file_not_found per DECLARED Preload path still absent after
    /// materialization; and a single preload_empty when a brief declared Preload paths but loaded zero
    /// (the signal that would have caught experiment 2's silent no-op on ticket 1). All ADVISORY: this
    /// emits and returns; the caller proceeds. The chain's hard-fail comes from a phase return value,
    /// never the event stream, so emitting GateFailure here is pure telemetry (same as the sibling
    /// drift_warning/hygiene_gate advisories). Gated by [project].preload_context: off -> "" and NO
    /// events, leaving the brief byte-identical to the pre-preload baseline.
    /// </summary>
    internal async Task<string> BuildAndReportPreloadAsync(
        string ticketId, string? descriptionHtml, Func<string, string?> readFile, CancellationToken ct)
    {
        if (!_project.PreloadContext)
            return string.Empty;

        var result = PreloadedContextBuilder.Build(descriptionHtml, _project, readFile);

        // Per-ticket summary (mirrors the gate's per-ticket CostLedger). The event log is the system of
        // record; the worker's prompt gets none of this enumeration.
        await EmitAsync(EventKind.CostLedger, ticketId, new Dictionary<string, object>
        {
            ["kind"] = "preload_summary",
            ["files_requested"] = result.FilesRequested,
            ["files_loaded"] = result.FilesLoaded,
            ["files_truncated"] = result.FilesTruncated,
            ["bytes_loaded"] = result.TotalBytes,
            ["not_found"] = result.NotFoundAll.ToList(),
            ["truncated"] = result.LoadedTruncated.ToList(),
            ["omitted"] = result.Omitted.ToList(),
        }, ct).ConfigureAwait(false);

        // A path the brief NAMED that is absent even after the worktree is materialized is genuinely
        // suspicious (convention-only absence stays in the summary's not_found list, no loud event).
        foreach (var file in result.NotFoundNamed)
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "preload_file_not_found",
                ["file"] = file,
            }, ct).ConfigureAwait(false);
        }

        // Declared Preload paths but loaded zero - the single signal that catches a no-opped pre-load.
        if (result.DeclaredButAllMissing)
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "preload_empty",
                ["requested"] = result.FilesRequested,
            }, ct).ConfigureAwait(false);
        }

        return result.Section;
    }

    private async Task<ReworkBriefContext> BuildReworkBriefContextAsync(
        IReadOnlyList<TicketComment> comments,
        string baseRef,
        string branchName,
        string workingDirectory,
        CancellationToken ct)
    {
        var summary = LatestImplementedSummary(comments);
        IReadOnlyList<string> touchedFiles = Array.Empty<string>();

        try
        {
            var diff = await _git.DiffAsync(
                baseRef,
                branchName,
                workingDirectory,
                includePatchContent: false,
                ct).ConfigureAwait(false);
            touchedFiles = diff.Entries.Select(e => e.Path).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Rework enrichment is a token-economy hint. If git cannot provide it,
            // keep the cold-boot behavior instead of failing the phase.
        }

        return new ReworkBriefContext(summary, touchedFiles);
    }

    private static string? LatestImplementedSummary(IReadOnlyList<TicketComment> comments)
    {
        TicketComment? latest = null;
        foreach (var comment in comments)
        {
            if (MarkerParser.Parse(comment.Body).Any(m => m.Name == "implemented_at") &&
                (latest is null || comment.CreatedAt >= latest.CreatedAt))
            {
                latest = comment;
            }
        }

        if (latest is null)
            return null;

        var withoutMarker = ImplementedAtParagraphPattern.Replace(latest.Body, "");
        var plain = HtmlTagPattern.Replace(withoutMarker, "\n");
        plain = WebUtility.HtmlDecode(plain);
        var lines = plain
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        var summary = string.Join("\n", lines).Trim();
        return summary.Length == 0 ? null : summary;
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

    private static string FormatDirtyWorktreeReason(string phase, IReadOnlyList<string> dirtyPaths, int sampleLimit = 5)
    {
        var sample = dirtyPaths.Take(sampleLimit).ToList();
        var sampleStr = string.Join(", ", sample);
        var morePart = dirtyPaths.Count > sampleLimit ? $"; ... and {dirtyPaths.Count - sampleLimit} more" : "";
        return $"{phase}: worktree dirty after worker exit - {dirtyPaths.Count} file(s) uncommitted: {sampleStr}{morePart}";
    }

    // Resolves completion_claim_ref from metadata and parses the referenced block as a CompletionClaim.
    // Returns false with a diagnostic error when the ref is absent, the block is missing or empty,
    // or the JSON does not satisfy the CompletionClaim schema.
    private static bool TryExtractClaim(
        IReadOnlyDictionary<string, object> metadata,
        IReadOnlyDictionary<string, string>? blocks,
        out CompletionClaim? claim,
        out string? error)
    {
        claim = null;
        error = null;
        IReadOnlyDictionary<string, string> effectiveBlocks =
            blocks ?? new Dictionary<string, string>();
        if (!FencedBlockResolver.TryResolveRef(effectiveBlocks, metadata, "completion_claim_ref", out var claimJson, out var refError))
        {
            error = refError;
            return false;
        }
        if (string.IsNullOrWhiteSpace(claimJson))
        {
            error = "COMPLETION_CLAIM block is empty";
            return false;
        }
        return CompletionClaimParser.TryParse(claimJson!, out claim, out error);
    }

    // Builds a minimal re-ask brief instruction asking the worker to emit only the COMPLETION_CLAIM
    // block for already-committed work. Deliberately does not re-present the full implementation
    // context to keep token cost low.
    private static string BuildClaimReAskInstruction(string ticketId, string branchName, string? commitSha)
    {
        var shaNote = commitSha is not null ? $" at commit {commitSha}" : "";
        return
            $"[Claim re-emit only]\n\n" +
            $"Your implementation for ticket {ticketId} has been committed to branch {branchName}{shaNote}. " +
            $"The WORKER_RESULT you emitted did not include a valid COMPLETION_CLAIM block.\n\n" +
            $"Emit ONLY the COMPLETION_CLAIM block for your already-committed work. " +
            $"Do NOT make any additional code changes or commits.\n\n" +
            $"<<<COMPLETION_CLAIM_START\n" +
            $"{{\"provides\":[],\"consumes\":[],\"ac_bindings\":[],\"tests_added\":[]}}\n" +
            $"<<<COMPLETION_CLAIM_END\n\n" +
            $"- `provides`: output artifact paths or module names this implementation delivers (may be empty)\n" +
            $"- `consumes`: input artifact paths or module names this implementation depends on (may be empty)\n" +
            $"- `ac_bindings`: array of {{\"ac_ref\":\"<label>\",\"kind\":\"<kind>\"}} objects binding AC items to verifier kinds; " +
            $"allowed kinds: Test, GrepPresent, GrepAbsent, File, Exit, Golden, Smoke (may be empty)\n" +
            $"- `tests_added`: test names or test file paths added in this implementation (may be empty)\n\n" +
            $"Then emit:\n\n" +
            $"WORKER_RESULT\n" +
            $"{{\"status\":\"Ok\",\"summary\":\"Claim re-emit for {ticketId}\",\"files_changed\":[]," +
            $"\"failure_reason\":null,\"metadata\":{{\"commit_sha\":\"{commitSha ?? "unknown"}\"," +
            $"\"files_changed\":[],\"completion_claim_ref\":\"COMPLETION_CLAIM\"}}}}";
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }

    private static long? TryGetLong(IReadOnlyDictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is int i) return i;
        if (val is long l) return l;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.TryGetInt64(out var jl) ? jl : null;
        return null;
    }
}
