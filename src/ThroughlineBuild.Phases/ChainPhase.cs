using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public record ChainBatchImplementGroup(IReadOnlyList<string> TicketIds);

public record ChainPhaseOptions(
    string TicketId,
    bool Debug,
    Action<string, ChainStep>? OnStep = null,
    bool NoAutoResolve = false,
    string? SharedWorktreePath = null,
    ChainCommitRange? ChainCommitRange = null,
    ChainBatchImplementGroup? BatchImplementGroup = null);

public class ChainPhase
{
    private const int MaxReworkRounds = 2;
    private static readonly object DebugIndexLock = new();

    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> _implementFactory;
    private readonly Func<BuildOptions, ReviewPhase> _reviewFactory;
    private readonly Func<BuildOptions, ShipPhase> _shipFactory;
    // Ship factory used within the parent-chain path: produces a ShipPhase with SkipDecruft=true
    // so the shared worktree is not torn down after each ticket. Falls back to _shipFactory when null.
    private readonly Func<BuildOptions, ShipPhase>? _chainShipFactory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly string _workingDirectory;
    private readonly BuildOptions _baseOptions;
    private readonly Func<BuildOptions, IObsoleteRatifier>? _ratifierFactory;
    private readonly IGitClient _git;
    // Optional: recovers the latest Rework verdict from the event log so an in-progress ticket
    // that carries real work can be resumed with its prior feedback. Null falls back to a
    // synthesized resume note (e.g. an interrupted initial implement that was never reviewed).
    private readonly IReviewFeedbackRetriever? _feedbackRetriever;
    // Optional: when set, batch implement groups in the parent chain dispatch one session here
    // instead of running a per-ticket implement+review+ship loop for each group member.
    private readonly IWorkerAgent? _batchWorker;

    public ChainPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions baseOptions,
        Func<BuildOptions, PlanPhase> planFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<string>? sessionIdGenerator = null,
        string? workingDirectory = null,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory = null,
        Func<BuildOptions, ShipPhase>? chainShipFactory = null,
        IGitClient? gitClient = null,
        IReviewFeedbackRetriever? feedbackRetriever = null,
        IWorkerAgent? batchWorker = null)
    {
        _ticketing = ticketing;
        _events = events;
        _baseOptions = baseOptions;
        _planFactory = planFactory;
        _implementFactory = implementFactory;
        _reviewFactory = reviewFactory;
        _shipFactory = shipFactory;
        _chainShipFactory = chainShipFactory;
        _sessionIdGenerator = sessionIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _ratifierFactory = ratifierFactory;
        _git = gitClient ?? new ProcessGitClient();
        _feedbackRetriever = feedbackRetriever;
        _batchWorker = batchWorker;
    }

    public async Task<ChainResult> RunAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var steps = new List<ChainStep>();

        var chainSessionId = _sessionIdGenerator();

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        // Preflight hygiene gate: run ONCE at the outermost chain entry (children recurse
        // with SharedWorktreePath set, so they are skipped here). The stash stack is
        // repo-global and leaks across worktrees, so a dangling stash or conflict left on
        // the tree can corrupt a ticket mid-chain. Catch it here - before any planning -
        // instead of burning a plan round and failing opaquely inside implement.
        if (options.SharedWorktreePath is null)
        {
            // Wrong-branch guard: the chain ends by shipping into _baseOptions.TargetBranch,
            // and ShipPhase performs that merge by advancing whatever HEAD the MAIN worktree
            // points at (FastForwardMergeAsync). If the main worktree is parked on a different
            // branch (or detached), the ship gate refuses - but only after plan/implement/review
            // have already burned minutes of work. Mirror that ship preflight here, before any
            // planning, so the operator fixes the branch up front instead of discovering it at
            // the very end. The two checks compare against the same target branch and must agree.
            var targetBranch = _baseOptions.TargetBranch;
            var currentBranch = await _git.CurrentBranchAsync(_workingDirectory, ct).ConfigureAwait(false);
            if (!string.Equals(currentBranch, targetBranch, StringComparison.Ordinal))
            {
                var wrongBranchMessage =
                    $"{_workingDirectory} is on '{currentBranch}' (or detached); the chain ships into " +
                    $"'{targetBranch}', so the main worktree must be on '{targetBranch}' before starting. " +
                    $"Switch with 'git switch {targetBranch}' and re-run.";

                Console.Error.WriteLine($"[{options.TicketId}] chain refused: {wrongBranchMessage}");
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: chainSessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.GateFailure,
                    TicketId: options.TicketId,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["kind"] = "chain_preflight_wrong_branch",
                        ["expected"] = targetBranch,
                        ["actual"] = currentBranch,
                        ["worktree"] = _workingDirectory
                    }), ct).ConfigureAwait(false);
                totalSw.Stop();
                var refused = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedWrongBranch,
                    totalSw.Elapsed, wrongBranchMessage);
                await EmitChainEndAsync(refused, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return refused;
            }

            var preflightBranch = PhaseWorktreeLayout.BranchName(ticket.Id);
            var preflightFailure = await WorkingTreeHygieneGate
                .CheckAsync(_git, _workingDirectory, preflightBranch, ct).ConfigureAwait(false);
            if (preflightFailure is not null)
            {
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: chainSessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.GateFailure,
                    TicketId: options.TicketId,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["kind"] = "hygiene_gate_preflight",
                        ["detail"] = preflightFailure
                    }), ct).ConfigureAwait(false);
                totalSw.Stop();
                var refused = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedDirtyTree,
                    totalSw.Elapsed, preflightFailure, DirtyTreeCause: DirtyTreeCause.Hygiene);
                await EmitChainEndAsync(refused, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return refused;
            }

            var dirtyTrackedPaths = await _git.GetTrackedChangesAsync(_workingDirectory, ct).ConfigureAwait(false);
            if (dirtyTrackedPaths.Count > 0)
            {
                const int dirtyPathSampleLimit = 25;
                var dirtyPathSample = dirtyTrackedPaths.Take(dirtyPathSampleLimit).ToList();
                var dirtyPathList = string.Join(", ", dirtyPathSample);
                var more = dirtyTrackedPaths.Count > dirtyPathSample.Count
                    ? $" (+{dirtyTrackedPaths.Count - dirtyPathSample.Count} more)"
                    : "";
                var dirtyMessage =
                    $"{_workingDirectory} has {dirtyTrackedPaths.Count} modified tracked files: " +
                    $"{dirtyPathList}{more}. Commit, stash, or revert them before running build chain.";

                Console.Error.WriteLine($"[{options.TicketId}] chain refused: {dirtyMessage}");
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: chainSessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.GateFailure,
                    TicketId: options.TicketId,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["kind"] = "chain_preflight_dirty",
                        ["dirty_count"] = dirtyTrackedPaths.Count,
                        ["dirty_paths"] = dirtyPathSample,
                        ["worktree"] = _workingDirectory
                    }), ct).ConfigureAwait(false);
                totalSw.Stop();
                var refused = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedDirtyTree,
                    totalSw.Elapsed, dirtyMessage, DirtyTreeCause: DirtyTreeCause.TrackedChanges);
                await EmitChainEndAsync(refused, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return refused;
            }
        }

        // Parent-ticket chain path: recurse to non-terminal children
        var chainChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (chainChildren.Count > 0)
        {
            return await RunParentChainAsync(options, ticket, chainChildren, ct).ConfigureAwait(false);
        }

        // Resolve where the chain enters based on the ticket's current state. Backlog/Ready/InReview
        // map directly to plan/implement/review. Planning and InProgress are non-terminal "stuck"
        // states an interrupted plan/implement leaves behind; the chain resumes them (reconciling any
        // orphaned branch/worktree first) rather than refusing. Only the terminal Done/Cancelled
        // states are genuinely un-runnable. ResolveEntryAsync performs any reset/prune side effects.
        var entry = await ResolveEntryAsync(ticket, chainSessionId, ct).ConfigureAwait(false);
        var startPhase = entry.StartPhase;

        var startingAtPhaseStr = startPhase switch
        {
            StartPhase.Plan => "plan",
            StartPhase.Implement => "implement",
            StartPhase.ResumeImplement => "implement",
            StartPhase.Review => "review",
            _ => "refused"
        };

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ChainStart,
            TicketId: options.TicketId,
            Phase: Phase.Chain,
            Data: new Dictionary<string, object>
            {
                ["starting_at_phase"] = startingAtPhaseStr,
                ["initial_state"] = ticket.State.ToString(),
                ["chain_session_id"] = chainSessionId
            }), ct).ConfigureAwait(false);

        if (startPhase == StartPhase.Refused)
        {
            totalSw.Stop();
            var refusedResult = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedInitialState,
                totalSw.Elapsed, null);
            await EmitChainEndAsync(refusedResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
            return refusedResult;
        }

        if (startPhase == StartPhase.Plan)
        {
            var sessionId = _sessionIdGenerator();
            var buildOpts = BuildPhaseOptions(sessionId, options.TicketId, "plan");
            EmitPhaseStart(options, "plan", -1, sessionId);
            var sw = Stopwatch.StartNew();
            var planResult = await _planFactory(buildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
                .ConfigureAwait(false);
            sw.Stop();

            var planStep = new ChainStep(
                PhaseName: "plan",
                ReworkRoundNumber: -1,
                Status: planResult.Success ? Status.Ok : Status.Failed,
                FailureReason: planResult.FailureReason,
                Verdict: null,
                Duration: sw.Elapsed,
                PhaseSessionId: sessionId);
            steps.Add(planStep);
            options.OnStep?.Invoke(options.TicketId, planStep);

            if (!planResult.Success)
            {
                if (!options.NoAutoResolve &&
                    _ratifierFactory is not null &&
                    planResult.EscalationWorkerResult is not null &&
                    IsObsoleteEscalation(planResult.EscalationWorkerResult))
                {
                    // Plan runs in the main working directory (no worktree exists yet), so its
                    // obsolete evidence resolves there; null defers to the ratifier's own dir.
                    var ratifyVerdict = await RunRatificationAsync(options, steps, planResult.EscalationWorkerResult, evidenceDirectory: null, ct)
                        .ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence = ExtractSubsumedByEvidence(planResult.EscalationWorkerResult);
                        var finalRationale = FormatSubsumedRationale(evidence);
                        await _ticketing.TransitionAsync(options.TicketId, TicketState.Done, ct).ConfigureAwait(false);
                        await _ticketing.CreateCommentAsync(options.TicketId, "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>", ct).ConfigureAwait(false);
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: chainSessionId,
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.TicketSubsumed,
                            TicketId: options.TicketId,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }), ct).ConfigureAwait(false);
                        totalSw.Stop();
                        var ratified = new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence);
                        await EmitChainEndAsync(ratified, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                        return ratified;
                    }
                    // Ratifier rejected - fall through to StoppedAtPlan
                }
                totalSw.Stop();
                var stoppedAtPlan = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtPlan,
                    totalSw.Elapsed, null);
                await EmitChainEndAsync(stoppedAtPlan, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return stoppedAtPlan;
            }
        }

        if (startPhase == StartPhase.Plan || startPhase == StartPhase.Implement || startPhase == StartPhase.ResumeImplement)
        {
            // ResumeImplement re-enters the loop as a rework round (carries recovered/synthesized
            // feedback at round >= 1), so ImplementPhase reuses the in-progress worktree instead of
            // creating a fresh one. Plan/Implement start a clean initial round.
            var startRound = startPhase == StartPhase.ResumeImplement ? entry.ResumeStartRound : 0;
            var initialFeedback = startPhase == StartPhase.ResumeImplement ? entry.ResumeFeedback : null;
            var chainResult = await RunImplementReviewLoopAsync(options, steps, chainSessionId, startRound, initialFeedback, totalSw, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                var finalResult = chainResult with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
        }
        else
        {
            var chainResult = await RunReviewBranchAsync(options, steps, chainSessionId, 0, totalSw, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                var finalResult = chainResult with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
        }

        var shipSessionId = _sessionIdGenerator();
        var shipBuildOpts = BuildPhaseOptions(shipSessionId, options.TicketId, "ship");
        EmitPhaseStart(options, "ship", -1, shipSessionId);
        var shipSw = Stopwatch.StartNew();
        // When running inside a parent-chain shared worktree, use the chain ship factory (SkipDecruft=true).
        var activeShipFactory = (options.SharedWorktreePath is not null && _chainShipFactory is not null)
            ? _chainShipFactory
            : _shipFactory;
        var shipResult = await activeShipFactory(shipBuildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
            .ConfigureAwait(false);
        shipSw.Stop();

        var shipStep = new ChainStep(
            PhaseName: "ship",
            ReworkRoundNumber: -1,
            Status: shipResult.Success ? Status.Ok : Status.Failed,
            FailureReason: shipResult.FailureReason,
            Verdict: null,
            Duration: shipSw.Elapsed,
            PhaseSessionId: shipSessionId);
        steps.Add(shipStep);
        options.OnStep?.Invoke(options.TicketId, shipStep);

        totalSw.Stop();
        if (!shipResult.Success)
        {
            var stoppedAtShip = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtShip,
                totalSw.Elapsed, null);
            await EmitChainEndAsync(stoppedAtShip, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
            return stoppedAtShip;
        }

        var completed = new ChainResult(options.TicketId, steps, ChainOutcome.Completed, totalSw.Elapsed, null);
        await EmitChainEndAsync(completed, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
        return completed;
    }

    // Emits a pre-run START notice through the OnStep stream so the operator sees a
    // phase has begun, not just its completion line. Start markers are console-only:
    // they are never added to the steps list, so the returned ChainResult is unchanged.
    private static void EmitPhaseStart(ChainPhaseOptions options, string phaseName, int reworkRoundNumber, string sessionId)
    {
        options.OnStep?.Invoke(options.TicketId, new ChainStep(
            PhaseName: phaseName,
            ReworkRoundNumber: reworkRoundNumber,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: null,
            Duration: TimeSpan.Zero,
            PhaseSessionId: sessionId,
            IsStart: true));
    }

    private async Task EmitChainEndAsync(ChainResult result, string chainSessionId, string ticketId, CancellationToken ct)
    {
        var reworkRounds = result.Steps.Count(s => s.PhaseName == "implement" && s.ReworkRoundNumber >= 1);
        var data = new Dictionary<string, object>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["phases_run"] = result.Steps.Count,
            ["rework_rounds"] = reworkRounds,
            ["total_duration_ms"] = (long)result.TotalDuration.TotalMilliseconds
        };
        var preview = RationalePreview(result.FinalRationale);
        if (preview != null)
            data["final_rationale_preview"] = preview;

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ChainEnd,
            TicketId: ticketId,
            Phase: Phase.Chain,
            Data: data), ct).ConfigureAwait(false);
    }

    private async Task<ChainResult?> RunImplementReviewLoopAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int startRound,
        ReviewFeedback? initialFeedback,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        int round = startRound;
        ReviewFeedback? feedback = initialFeedback;

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = BuildPhaseOptions(implSessionId, options.TicketId, "implement", round);
            // Pass the chain's prior-commit pointer on the first implement round only.
            // Rework rounds (feedback != null) reuse the same worktree with the agent's
            // own edits already in place, so replaying the handoff is redundant.
            var implChainRange = (feedback is null) ? options.ChainCommitRange : null;
            var implPhaseOpts = new ImplementPhaseOptions(feedback, options.SharedWorktreePath, implChainRange);
            EmitPhaseStart(options, "implement", round, implSessionId);
            var implSw = Stopwatch.StartNew();
            var implResult = await _implementFactory(implBuildOpts, implPhaseOpts)
                .RunAsync(options.TicketId, _workingDirectory, ct).ConfigureAwait(false);
            implSw.Stop();

            var implStep = new ChainStep(
                PhaseName: "implement",
                ReworkRoundNumber: round,
                Status: implResult.Success ? Status.Ok : Status.Failed,
                FailureReason: implResult.FailureReason,
                Verdict: null,
                Duration: implSw.Elapsed,
                PhaseSessionId: implSessionId);
            steps.Add(implStep);
            options.OnStep?.Invoke(options.TicketId, implStep);

            if (!implResult.Success)
            {
                if (!options.NoAutoResolve &&
                    _ratifierFactory is not null &&
                    implResult.EscalationWorkerResult is not null &&
                    IsObsoleteEscalation(implResult.EscalationWorkerResult))
                {
                    // Implement (incl. rework) runs in the ticket's worktree, where the cited
                    // commit and any new files actually live - resolve evidence against it.
                    var ratifyVerdict = await RunRatificationAsync(options, steps, implResult.EscalationWorkerResult, implResult.WorktreePath, ct)
                        .ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence = ExtractSubsumedByEvidence(implResult.EscalationWorkerResult);
                        var finalRationale = FormatSubsumedRationale(evidence);
                        await _ticketing.TransitionAsync(options.TicketId, TicketState.Done, ct).ConfigureAwait(false);
                        await _ticketing.CreateCommentAsync(options.TicketId, "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>", ct).ConfigureAwait(false);
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: chainSessionId,
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.TicketSubsumed,
                            TicketId: options.TicketId,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }), ct).ConfigureAwait(false);
                        totalSw.Stop();
                        return new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence);
                    }
                    // Ratifier rejected - fall through to StoppedAtImplement
                }
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtImplement,
                    TimeSpan.Zero, null);
            }

            var reviewResult = await RunOneReviewAsync(options, steps, round, ct).ConfigureAwait(false);

            if (reviewResult.abort is not null)
                return reviewResult.abort;

            var rv = reviewResult.verdict!;

            if (rv.Kind == VerdictKind.Pass)
                return null;

            if (rv.Kind == VerdictKind.Fail)
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                    TimeSpan.Zero, rv.Rationale);

            if (round < MaxReworkRounds)
            {
                feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1);
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: chainSessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.ReworkRound,
                    TicketId: options.TicketId,
                    Phase: Phase.Implement,
                    Data: new Dictionary<string, object>
                    {
                        ["round"] = round + 1,
                        ["verdict_that_triggered"] = "Rework",
                        ["rationale_preview"] = RationalePreview(rv.Rationale) ?? ""
                    }), ct).ConfigureAwait(false);
                round++;
            }
            else
            {
                return new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                    TimeSpan.Zero, rv.Rationale);
            }
        }
    }

    private async Task<ChainResult?> RunReviewBranchAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int round,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        var reviewResult = await RunOneReviewAsync(options, steps, round, ct).ConfigureAwait(false);

        if (reviewResult.abort is not null)
            return reviewResult.abort;

        var rv = reviewResult.verdict!;

        if (rv.Kind == VerdictKind.Pass)
            return null;

        if (rv.Kind == VerdictKind.Fail)
            return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, rv.Rationale);

        var feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1);
        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ReworkRound,
            TicketId: options.TicketId,
            Phase: Phase.Implement,
            Data: new Dictionary<string, object>
            {
                ["round"] = round + 1,
                ["verdict_that_triggered"] = "Rework",
                ["rationale_preview"] = RationalePreview(rv.Rationale) ?? ""
            }), ct).ConfigureAwait(false);
        return await RunImplementReviewLoopAsync(options, steps, chainSessionId, round + 1, feedback, totalSw, ct)
            .ConfigureAwait(false);
    }

    private async Task<(ChainResult? abort, Verdict? verdict)> RunOneReviewAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        int round,
        CancellationToken ct)
    {
        var revSessionId = _sessionIdGenerator();
        var revBuildOpts = BuildPhaseOptions(revSessionId, options.TicketId, "review", round);
        EmitPhaseStart(options, "review", -1, revSessionId);
        var revSw = Stopwatch.StartNew();
        var revResult = await _reviewFactory(revBuildOpts)
            .RunAsync(options.TicketId, _workingDirectory, ct).ConfigureAwait(false);
        revSw.Stop();

        if (!revResult.Success)
        {
            var failedRevStep = new ChainStep(
                PhaseName: "review",
                ReworkRoundNumber: -1,
                Status: Status.Failed,
                FailureReason: revResult.FailureReason,
                Verdict: null,
                Duration: revSw.Elapsed,
                PhaseSessionId: revSessionId);
            steps.Add(failedRevStep);
            options.OnStep?.Invoke(options.TicketId, failedRevStep);
            return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, revResult.FailureReason), null);
        }

        var revStep = new ChainStep(
            PhaseName: "review",
            ReworkRoundNumber: -1,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: revResult.Verdict,
            Duration: revSw.Elapsed,
            PhaseSessionId: revSessionId);
        steps.Add(revStep);
        options.OnStep?.Invoke(options.TicketId, revStep);

        return (null, new Verdict(revResult.Verdict!.Value, revResult.VerdictRationale ?? "", revResult.ChecksFailed));
    }

    private static string FormatSubsumedRationale(SubsumedByEvidence? evidence)
    {
        var commit = evidence?.Commit ?? "(unknown)";
        var rationale = evidence?.Rationale ?? "(no rationale)";
        var files = evidence?.Files is { Count: > 0 } f ? string.Join(", ", f) : "(none)";
        return $"Subsumed by {commit}: {rationale}; files: {files}";
    }

    private static string? RationalePreview(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return null;
        return rationale.Length <= 200 ? rationale : rationale.Substring(0, 200);
    }

    private BuildOptions BuildPhaseOptions(string sessionId, string ticketId, string phaseName, int? round = null)
    {
        var debugCaptureDirectory = ScopeDebugCaptureDirectory(
            _baseOptions.DebugCaptureDirectory,
            ticketId,
            phaseName,
            round,
            sessionId);

        if (_baseOptions.ProgressDigestSink is null)
        {
            return _baseOptions with
            {
                SessionId = sessionId,
                DebugCaptureDirectory = debugCaptureDirectory
            };
        }

        return _baseOptions with
        {
            SessionId = sessionId,
            DebugCaptureDirectory = debugCaptureDirectory,
            ProgressDigestSink = new PrefixedTextWriter($"[{ticketId}] ", _baseOptions.ProgressDigestSink)
        };
    }

    private static string? ScopeDebugCaptureDirectory(
        string? parentDirectory,
        string ticketId,
        string phaseName,
        int? round,
        string sessionId)
    {
        if (parentDirectory is null)
            return null;

        var attemptSegment = round is null ? SafePathSegment(sessionId) : $"round-{round.Value}";
        var scopedDirectory = Path.Combine(
            parentDirectory,
            SafePathSegment(ticketId),
            SafePathSegment(phaseName),
            attemptSegment);

        WriteDebugSessionIndex(parentDirectory, ticketId, phaseName, round, sessionId, scopedDirectory);
        return scopedDirectory;
    }

    private static void WriteDebugSessionIndex(
        string parentDirectory,
        string ticketId,
        string phaseName,
        int? round,
        string sessionId,
        string scopedDirectory)
    {
        try
        {
            Directory.CreateDirectory(parentDirectory);
            var relativePath = Path.GetRelativePath(parentDirectory, scopedDirectory);
            var roundLabel = round is null ? "-" : round.Value.ToString();
            var line = $"{DateTimeOffset.UtcNow:O}\t{ticketId}\t{phaseName}\t{roundLabel}\t{sessionId}\t{relativePath}{Environment.NewLine}";
            lock (DebugIndexLock)
            {
                File.AppendAllText(Path.Combine(parentDirectory, "session-index.txt"), line);
            }
        }
        catch
        {
            // Debug capture must never change phase behavior.
        }
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    private static bool IsObsoleteEscalation(WorkerResult r)
    {
        if (r.Status != Status.Escalate) return false;
        if (!r.Metadata.TryGetValue("escalation", out var escalationObj)) return false;
        if (escalationObj is not JsonElement escalationElem ||
            escalationElem.ValueKind != JsonValueKind.Object) return false;
        if (!escalationElem.TryGetProperty("reason", out var reasonElem)) return false;
        if (reasonElem.ValueKind != JsonValueKind.String) return false;
        return string.Equals(reasonElem.GetString(), "obsolete", StringComparison.OrdinalIgnoreCase);
    }

    private static SubsumedByEvidence? ExtractSubsumedByEvidence(WorkerResult r)
    {
        if (!r.Metadata.TryGetValue("escalation", out var escalationObj)) return null;
        if (escalationObj is not JsonElement escalationElem ||
            escalationElem.ValueKind != JsonValueKind.Object) return null;
        if (!escalationElem.TryGetProperty("subsumed_by", out var subsumedByElem) ||
            subsumedByElem.ValueKind != JsonValueKind.Object) return null;

        var commit = subsumedByElem.TryGetProperty("commit", out var commitElem) &&
                     commitElem.ValueKind == JsonValueKind.String
            ? commitElem.GetString() : null;
        var rationale = subsumedByElem.TryGetProperty("rationale", out var rationaleElem) &&
                        rationaleElem.ValueKind == JsonValueKind.String
            ? rationaleElem.GetString() : null;

        if (string.IsNullOrEmpty(commit) || string.IsNullOrEmpty(rationale)) return null;

        var files = new List<string>();
        if (subsumedByElem.TryGetProperty("files", out var filesElem) &&
            filesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElem in filesElem.EnumerateArray())
            {
                if (fileElem.ValueKind == JsonValueKind.String)
                {
                    var f = fileElem.GetString();
                    if (!string.IsNullOrEmpty(f)) files.Add(f);
                }
            }
        }

        return new SubsumedByEvidence(commit, files.AsReadOnly(), rationale);
    }

    private async Task<Verdict> RunRatificationAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        WorkerResult escalateResult,
        string? evidenceDirectory,
        CancellationToken ct)
    {
        var sessionId = _sessionIdGenerator();
        var buildOpts = BuildPhaseOptions(sessionId, options.TicketId, "ratify");
        var ratifier = _ratifierFactory!(buildOpts);

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        EmitPhaseStart(options, "ratify", -1, sessionId);
        var sw = Stopwatch.StartNew();
        var verdict = await ratifier.RatifyAsync(ticket, escalateResult, evidenceDirectory, ct).ConfigureAwait(false);
        sw.Stop();

        var ratifyStep = new ChainStep(
            PhaseName: "ratify",
            ReworkRoundNumber: -1,
            Status: verdict.Kind == VerdictKind.Pass ? Status.Ok : Status.Failed,
            FailureReason: verdict.Kind != VerdictKind.Pass ? verdict.Rationale : null,
            Verdict: verdict.Kind,
            Duration: sw.Elapsed,
            PhaseSessionId: sessionId);
        steps.Add(ratifyStep);
        options.OnStep?.Invoke(options.TicketId, ratifyStep);

        return verdict;
    }

    /// <summary>
    /// Runs a single implement session for all tickets in the batch group, then returns one
    /// <see cref="ChainResult"/> per ticket with outcome <see cref="ChainOutcome.BatchImplemented"/>.
    /// The session executes inside the already-created shared chain worktree. All batch commits
    /// stack on the first ticket's branch; review and ship are left to future briefs (Brief 05/06).
    /// </summary>
    private async Task<IReadOnlyList<ChainResult>> RunBatchImplementSessionAsync(
        ChainPhaseOptions options,
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        string? chainStartSha,
        CancellationToken ct)
    {
        var batchSw = Stopwatch.StartNew();

        // Create the first ticket's branch in the shared worktree; all batch commits stack on it.
        var firstTicket = batchTickets[0];
        var batchBranchName = PhaseWorktreeLayout.BranchName(firstTicket.Id);
        var branchResult = await _git.CreateBranchAsync(
            batchBranchName, baseRef, sharedWorktreePath, ct).ConfigureAwait(false);
        if (!branchResult.Success)
        {
            batchSw.Stop();
            var branchFail = new ChainResult(
                firstTicket.Id, Array.Empty<ChainStep>(),
                ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                $"batch implement: branch create for {batchBranchName} failed: {branchResult.FailureReason}");
            return new[] { branchFail };
        }

        // Transition all batch tickets Ready -> InProgress to mark that work has started.
        foreach (var ticket in batchTickets)
        {
            try
            {
                await _ticketing.TransitionAsync(ticket.Id, TicketState.InProgress, ct)
                    .ConfigureAwait(false);
            }
            catch { /* non-fatal: transition failure must not block the batch session */ }
        }

        // Build the chain commit range for the brief (best-effort; null is safe).
        ChainCommitRange? batchCommitRange = null;
        if (chainStartSha is not null)
        {
            try
            {
                var (_, currentTargetSha) = await BaseRefResolver.ResolveAsync(
                    _git, _workingDirectory, _baseOptions.TargetBranch, ct).ConfigureAwait(false);
                batchCommitRange = await ChainCommitRangeHelper.ComputeAsync(
                    _git, chainStartSha, currentTargetSha, _workingDirectory, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        }

        // Build the RepoState for the brief.
        string mainSha;
        try
        {
            mainSha = await _git.RevParseAsync(baseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            mainSha = string.Empty;
        }
        var topLevelEntries = Directory.EnumerateFileSystemEntries(_workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);

        // Build the batch implement brief.
        var batchSessionId = _sessionIdGenerator();
        var batchBrief = BatchImplementBriefBuilder.Build(
            _batchWorker!.Name,
            batchTickets,
            repoState,
            batchBranchName,
            sharedWorktreePath,
            batchCommitRange);

        // Emit start step for the batch session (associated with the first ticket for tracing).
        EmitPhaseStart(options with { TicketId = firstTicket.Id }, "batch-implement", -1, batchSessionId);

        var implSw = Stopwatch.StartNew();

        // Compute the max worker size across all batch tickets.
        var maxSize = batchTickets.Max(t => WorkerSizeMapper.FromTicketSize(t.Size));
        var batchBuildOpts = BuildPhaseOptions(batchSessionId, firstTicket.Id, "batch-implement");
        var workerOptions = new WorkerOptions(
            _baseOptions.WorkerTimeout,
            _baseOptions.WorkerAllowedTools,
            DebugCaptureDirectory: batchBuildOpts.DebugCaptureDirectory,
            LiveStdoutSink: _baseOptions.LiveStdoutSink,
            LiveStderrSink: _baseOptions.LiveStderrSink,
            ProgressDigestSink: _baseOptions.ProgressDigestSink,
            Size: maxSize);
        if (batchBuildOpts.DebugCaptureDirectory is not null)
            Directory.CreateDirectory(batchBuildOpts.DebugCaptureDirectory);

        var workerResult = await _batchWorker!
            .ExecuteAsync(batchBrief, sharedWorktreePath, workerOptions, ct)
            .ConfigureAwait(false);

        implSw.Stop();

        // Worker failed: return a StoppedAtImplement result for every ticket in the group.
        if (workerResult.Status == Status.Failed || workerResult.Status == Status.Escalate)
        {
            batchSw.Stop();
            return batchTickets.Select(t => new ChainResult(
                t.Id, Array.Empty<ChainStep>(),
                ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                workerResult.FailureReason ?? workerResult.Summary)).ToList().AsReadOnly();
        }

        // Worker succeeded: produce a BatchImplemented result for each ticket in the group.
        // Per-ticket commit SHAs come from workerResult.Tickets (populated by WorkerResultParser
        // when the batch-implement template's WORKER_RESULT JSON includes a "tickets" array).
        var perTicketResults = workerResult.Tickets;
        var results = new List<ChainResult>(batchTickets.Count);
        for (int i = 0; i < batchTickets.Count; i++)
        {
            var ticket = batchTickets[i];
            var perTicket = perTicketResults?.FirstOrDefault(
                r => string.Equals(r.TicketId, ticket.Id, StringComparison.Ordinal));

            var implStep = new ChainStep(
                PhaseName: "batch-implement",
                ReworkRoundNumber: 0,
                Status: Status.Ok,
                FailureReason: null,
                Verdict: null,
                Duration: implSw.Elapsed,
                PhaseSessionId: batchSessionId);

            results.Add(new ChainResult(
                TicketId: ticket.Id,
                Steps: new[] { implStep },
                Outcome: ChainOutcome.BatchImplemented,
                TotalDuration: batchSw.Elapsed,
                FinalRationale: perTicket is not null
                    ? $"batch implement succeeded; commit {perTicket.CommitSha}"
                    : "batch implement succeeded"));
        }

        return results.AsReadOnly();
    }

    private async Task<ChainResult> RunParentChainAsync(
        ChainPhaseOptions options,
        Ticket parentTicket,
        IReadOnlyList<Ticket> children,
        CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // Filter to non-terminal children (skip Done and Cancelled), and never the parent
        // itself - a self-referential parent edge would otherwise recurse forever.
        // Order by ascending ticket number: TopologicalSorter preserves input order as its
        // within-level tiebreaker, so feeding it lowest-number-first makes unordered siblings
        // (same dependency level, no blocked_by edge) dispatch lowest-number-first.
        var eligible = children
            .Where(c => c.State != TicketState.Done && c.State != TicketState.Cancelled)
            .Where(c => !string.Equals(c.Uuid, parentTicket.Uuid, StringComparison.Ordinal))
            .OrderBy(c => TicketNumber(c.Id))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // A parent chain operates exactly one level deep: it runs its direct children.
        // If any eligible child has live children of its own, the tree is deeper than this
        // command handles. Stop and tell the operator to chain the intermediate ticket
        // directly, rather than recursing (which previously ran away into Plane's rate limiter).
        var deeperTickets = (await Task.WhenAll(eligible.Select(async child =>
        {
            var grandchildren = await _ticketing
                .QueryAsync(new TicketQuery(ParentId: child.Uuid), ct).ConfigureAwait(false);
            var hasLiveGrandchildren = grandchildren
                .Where(g => g.State != TicketState.Done && g.State != TicketState.Cancelled)
                .Any(g => !string.Equals(g.Uuid, child.Uuid, StringComparison.Ordinal));
            return hasLiveGrandchildren ? child.Id : null;
        })).ConfigureAwait(false))
            .Where(id => id is not null)
            .Cast<string>()
            .ToList();

        if (deeperTickets.Count > 0)
        {
            var notice = new ChainStep(
                PhaseName: "chain",
                ReworkRoundNumber: -1,
                Status: Status.Failed,
                FailureReason: $"grandchildren present under {string.Join(", ", deeperTickets)}",
                Verdict: null,
                Duration: TimeSpan.Zero,
                PhaseSessionId: _sessionIdGenerator());
            options.OnStep?.Invoke(options.TicketId, notice);

            totalSw.Stop();
            return new ChainResult(
                TicketId: options.TicketId,
                Steps: Array.Empty<ChainStep>(),
                Outcome: ChainOutcome.ParentHasGrandchildren,
                TotalDuration: totalSw.Elapsed,
                FinalRationale: $"Tree is deeper than one level. Chain the intermediate ticket(s) directly: {string.Join(", ", deeperTickets)}.");
        }

        // Build a level-based execution schedule from sibling blocked_by relations so that
        // dependent siblings are serialized while independent siblings still run in order.
        var siblingGraph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
        var levels = TopologicalSorter.ComputeLevels(siblingGraph);

        // Print the dependency order derived from Plane before any phase runs so a wrong
        // or missing edge is visible up front. Tickets in the same level have no blocked_by
        // edge between them and are unordered relative to each other (Brief 17).
        PrintDispatchOrder(options.TicketId, levels);

        // Create one shared worktree for the entire parent chain. Each child ticket creates
        // its own branch inside this worktree; ship skips decruft so the worktree stays alive
        // until all children are done. The worktree is removed once here at chain end.
        var sharedWorktreeNames = PhaseWorktreeLayout.Compute(parentTicket.Id, parentTicket.Title, _workingDirectory);
        // Placeholder branch the shared worktree is created on. Children immediately switch the
        // worktree to their own ticket/<slug> branches, so this branch never receives commits -
        // it is pure scaffolding and is deleted at chain end (see cleanup below).
        var sharedChainBranch = $"chain/{sharedWorktreeNames.Slug}";
        string? sharedWorktreePath = null;
        string? baseRefForSharedWt = null;
        try
        {
            (baseRefForSharedWt, _) = await BaseRefResolver.ResolveAsync(_git, _workingDirectory, _baseOptions.TargetBranch, ct).ConfigureAwait(false);
        }
        catch
        {
            // If we cannot resolve the base ref we fall back to null and let each child
            // handle ref resolution independently (standalone worktree per ticket path).
            baseRefForSharedWt = null;
        }

        // Capture the target head SHA once at chain start so ChainCommitRangeHelper can
        // later compute the range of commits the chain has produced (Brief 08).
        string? chainStartSha = null;
        try
        {
            if (baseRefForSharedWt is not null)
                chainStartSha = await _git.RevParseAsync(baseRefForSharedWt, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal: chainStartSha stays null */ }

        if (baseRefForSharedWt is not null && eligible.Count > 0)
        {
            var createResult = await _git.CreateWorktreeAsync(
                sharedWorktreeNames.WorktreePath,
                sharedChainBranch,
                baseRefForSharedWt,
                _workingDirectory,
                ct).ConfigureAwait(false);

            // Self-heal a leftover placeholder branch from a prior chain run that removed its
            // shared worktree but never deleted the branch. Because chain/<slug> is only ever
            // scaffolding (children switch to their own branches immediately), a pre-existing one
            // is safe to delete and recreate. Without this, the stale branch makes creation collide
            // and forces every re-run into the degraded per-ticket fallback indefinitely.
            if (!createResult.Success)
            {
                var existing = await _git.ListLocalBranchesAsync(sharedChainBranch, _workingDirectory, ct).ConfigureAwait(false);
                if (existing.Any(b => string.Equals(b, sharedChainBranch, StringComparison.Ordinal)))
                {
                    await _git.DeleteBranchAsync(sharedChainBranch, force: true, _workingDirectory, ct).ConfigureAwait(false);
                    createResult = await _git.CreateWorktreeAsync(
                        sharedWorktreeNames.WorktreePath,
                        sharedChainBranch,
                        baseRefForSharedWt,
                        _workingDirectory,
                        ct).ConfigureAwait(false);
                }
            }

            if (createResult.Success)
            {
                sharedWorktreePath = sharedWorktreeNames.WorktreePath;
            }
            else
            {
                // Shared worktree could not be created (commonly: the path already exists from a
                // prior interrupted parent chain). The chain still runs, but each child now builds
                // in its own standalone worktree with per-ticket decruft - a meaningfully different
                // layout. Surface it loudly instead of degrading silently.
                Console.Error.WriteLine(
                    $"[{parentTicket.Id}] warning: shared chain worktree unavailable " +
                    $"({createResult.FailureReason}); falling back to per-ticket worktrees. " +
                    $"If a prior chain was interrupted, remove {sharedWorktreeNames.WorktreePath} and re-run.");
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: _sessionIdGenerator(),
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.GateFailure,
                    TicketId: parentTicket.Id,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["kind"] = "shared_worktree_unavailable",
                        ["detail"] = createResult.FailureReason ?? "unknown",
                        ["path"] = sharedWorktreeNames.WorktreePath
                    }), ct).ConfigureAwait(false);
            }
        }

        var allChildResults = new List<ChainResult>();
        bool anyStoppedEarly = false;

        // Batch implement branch: when a batch group is declared and a batch worker is wired in,
        // run ONE implement session for the whole group inside the shared worktree.
        // Only tickets that are in the eligible set and in Ready state join the batch;
        // tickets not in the group (or not in Ready state) are dispatched per-ticket below.
        HashSet<string>? batchedTicketIds = null;
        if (options.BatchImplementGroup is not null
            && _batchWorker is not null
            && sharedWorktreePath is not null
            && !anyStoppedEarly)
        {
            var batchGroup = options.BatchImplementGroup;
            var batchTickets = batchGroup.TicketIds
                .Where(id => eligible.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                .Select(id => eligible.First(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                .Where(t => t.State == TicketState.Ready)
                .ToList();

            if (batchTickets.Count > 0)
            {
                batchedTicketIds = new HashSet<string>(
                    batchTickets.Select(t => t.Id), StringComparer.Ordinal);

                var batchResults = await RunBatchImplementSessionAsync(
                    options, batchTickets, sharedWorktreePath, baseRefForSharedWt!, chainStartSha, ct)
                    .ConfigureAwait(false);

                foreach (var br in batchResults)
                {
                    allChildResults.Add(br);
                    if (!IsChainSuccess(br.Outcome))
                    {
                        anyStoppedEarly = true;
                        break;
                    }
                }
            }
        }

        foreach (var level in levels)
        {
            if (anyStoppedEarly)
                break;

            var levelTickets = level
                .Select(id => eligible.First(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
                .Where(child => batchedTicketIds is null || !batchedTicketIds.Contains(child.Id))
                .ToList();

            // Parent chains intentionally dispatch one child at a time. A successful child
            // ships into the local target before the next child resolves its base, so siblings
            // stack even when the dependency graph marks them as unordered.
            foreach (var child in levelTickets)
            {
                var startStep = new ChainStep(
                    PhaseName: "chain",
                    ReworkRoundNumber: -1,
                    Status: Status.Ok,
                    FailureReason: null,
                    Verdict: null,
                    Duration: TimeSpan.Zero,
                    PhaseSessionId: _sessionIdGenerator());
                options.OnStep?.Invoke(options.TicketId, startStep);

                // Derive the chain's prior-commit pointer before each ticket so the
                // implement brief lists the files already touched by shipped siblings.
                // Resolve the CURRENT base the same way the child's implement will
                // (BaseRefResolver advances to the local target tip as siblings ship
                // locally), so the range reflects the accumulated sibling commits rather
                // than the frozen origin (TLB-411). Best-effort: any git failure leaves
                // the pointer null, which is safe - the brief is identical to the
                // no-pointer baseline.
                ChainCommitRange? childCommitRange = null;
                if (chainStartSha is not null && baseRefForSharedWt is not null)
                {
                    try
                    {
                        var (_, currentTargetSha) = await BaseRefResolver.ResolveAsync(
                            _git, _workingDirectory, _baseOptions.TargetBranch, ct).ConfigureAwait(false);
                        childCommitRange = await ChainCommitRangeHelper.ComputeAsync(
                            _git, chainStartSha, currentTargetSha, _workingDirectory, ct).ConfigureAwait(false);
                    }
                    catch { /* non-fatal: pointer stays null */ }
                }

                var childOptions = options with
                {
                    TicketId = child.Id,
                    SharedWorktreePath = sharedWorktreePath,
                    ChainCommitRange = childCommitRange
                };
                var childResult = await RunAsync(childOptions, ct).ConfigureAwait(false);

                var ok = IsChainSuccess(childResult.Outcome);
                var doneStep = new ChainStep(
                    PhaseName: "chain",
                    ReworkRoundNumber: -1,
                    Status: ok ? Status.Ok : Status.Failed,
                    FailureReason: ok ? null : $"child {child.Id} stopped: {childResult.Outcome}",
                    Verdict: null,
                    Duration: childResult.TotalDuration,
                    PhaseSessionId: _sessionIdGenerator());
                options.OnStep?.Invoke(options.TicketId, doneStep);

                allChildResults.Add(childResult);

                if (!ok)
                {
                    anyStoppedEarly = true;
                    break;
                }
            }
        }

        var childResults = allChildResults;

        // Remove the shared worktree now that all children are done. Failure is non-fatal.
        if (sharedWorktreePath is not null)
        {
            try
            {
                var decrufter = new WorktreeDecrufter(_git);
                await decrufter.DecruftAsync(sharedWorktreePath, _workingDirectory, ct).ConfigureAwait(false);
                // The decrufter removes the worktree directory but never deletes branches. Delete the
                // chain/<slug> placeholder here so it does not leak and collide with the next run's
                // shared-worktree creation. force:true because it is unmerged scaffolding by design.
                await _git.DeleteBranchAsync(sharedChainBranch, force: true, _workingDirectory, ct).ConfigureAwait(false);

                // Delete each shipped child's ticket/<id> branch. Per-child ship leaves these in
                // place (the branch was checked out in the now-removed shared worktree, so it could
                // not be deleted in-flight). Only successfully-shipped children have a branch to drop;
                // RatifiedObsolete/stopped children either never cut one or carry unmerged work, so
                // they are skipped. force:true because the branch is merged into the local target by
                // the child's own ship - the same reason ShipPhase force-deletes (see Step 13).
                foreach (var childResult in allChildResults)
                {
                    if (childResult.Outcome != ChainOutcome.Completed)
                        continue;
                    var childBranch = PhaseWorktreeLayout.BranchName(childResult.TicketId);
                    await _git.DeleteBranchAsync(childBranch, force: true, _workingDirectory, ct).ConfigureAwait(false);
                }
            }
            catch { /* non-fatal: ticket transitions are already committed */ }
        }

        // Attempt parent rollup (fail-soft)
        try { await _ticketing.RollupParentAsync(options.TicketId, ct).ConfigureAwait(false); }
        catch { /* non-fatal */ }

        totalSw.Stop();
        var outcome = anyStoppedEarly ? ChainOutcome.ParentStoppedEarly : ChainOutcome.ParentCompleted;
        var finalRationale = anyStoppedEarly
            ? $"One or more children did not complete: {string.Join(", ", childResults.Where(r => !IsChainSuccess(r.Outcome)).Select(r => r.TicketId))}"
            : $"All {eligible.Count} eligible children completed.";

        return new ChainResult(
            TicketId: options.TicketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: totalSw.Elapsed,
            FinalRationale: finalRationale,
            ChildResults: childResults.AsReadOnly());
    }

    /// <summary>
    /// Prints the dependency-ordered dispatch sequence before the first phase runs.
    /// Each level is a set of tickets with no blocked_by edge between them; within a
    /// level they are unordered relative to each other, making a missing edge obvious.
    /// </summary>
    // Extracts the trailing integer from a ticket id like "TLB-369" -> 369 for numeric ordering.
    // Returns int.MaxValue when there is no parseable trailing number so malformed ids sort last
    // (after which ThenBy on the full id keeps ordering deterministic).
    private static int TicketNumber(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash >= 0 && dash < id.Length - 1 && int.TryParse(id.AsSpan(dash + 1), out var n))
            return n;
        return int.MaxValue;
    }

    private static void PrintDispatchOrder(string parentId, IReadOnlyList<IReadOnlyList<string>> levels)
    {
        Console.WriteLine($"[{parentId}] dispatch order ({levels.Count} level{(levels.Count == 1 ? "" : "s")}):");
        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var ticketList = string.Join(", ", level);
            var unorderedNote = level.Count > 1 ? " (unordered)" : "";
            Console.WriteLine($"  level {i + 1}: {ticketList}{unorderedNote}");
        }
    }

    private async Task<TicketGraph> BuildSiblingGraphAsync(IReadOnlyList<Ticket> eligible, CancellationToken ct)
    {
        var graph = new TicketGraph();
        var eligibleIdSet = new HashSet<string>(eligible.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in eligible)
            graph.AddNode(ticket.Id);

        var relationsByTicket = await Task.WhenAll(eligible.Select(async ticket => new
        {
            TicketId = ticket.Id,
            Relations = await _ticketing.GetRelationsAsync(ticket.Id, ct).ConfigureAwait(false)
        })).ConfigureAwait(false);

        foreach (var ticketRelations in relationsByTicket)
        {
            foreach (var rel in ticketRelations.Relations)
            {
                if (rel.Kind == "blocked_by" && eligibleIdSet.Contains(rel.TargetId))
                    graph.AddEdge(rel.TargetId, ticketRelations.TicketId); // TargetId is the blocker
            }
        }

        return graph;
    }

    private static bool IsChainSuccess(ChainOutcome outcome) =>
        outcome is ChainOutcome.Completed
            or ChainOutcome.RatifiedObsolete
            or ChainOutcome.ParentCompleted
            or ChainOutcome.BatchImplemented;

    /// <summary>
    /// Decides where the chain enters for a single (leaf) ticket and performs any state-reconciliation
    /// side effects required to make that entry valid. Backlog/Ready/InReview map directly; the
    /// non-terminal "stuck" states (Planning, InProgress) are resumed; Done/Cancelled are refused.
    /// </summary>
    private async Task<ChainEntry> ResolveEntryAsync(Ticket ticket, string chainSessionId, CancellationToken ct)
    {
        switch (ticket.State)
        {
            case TicketState.Backlog:
                return new ChainEntry(StartPhase.Plan, null, 0);
            case TicketState.Ready:
                return new ChainEntry(StartPhase.Implement, null, 0);
            case TicketState.InReview:
                return new ChainEntry(StartPhase.Review, null, 0);
            case TicketState.Planning:
                // Plan started but never finished: Backlog->Planning happens before the plan worker
                // runs, and no plan artifact is appended until it succeeds, so a Planning ticket has
                // nothing to preserve. Reset to Backlog and replan from scratch.
                await _ticketing.TransitionAsync(ticket.Id, TicketState.Backlog, ct).ConfigureAwait(false);
                await EmitResumeTransitionAsync(chainSessionId, ticket.Id, "Planning", "Backlog", ct).ConfigureAwait(false);
                return new ChainEntry(StartPhase.Plan, null, 0);
            case TicketState.InProgress:
                return await ResolveInProgressAsync(ticket, chainSessionId, ct).ConfigureAwait(false);
            default:
                return new ChainEntry(StartPhase.Refused, null, 0);
        }
    }

    /// <summary>
    /// Resolves how to resume an InProgress ticket. If the ticket's branch carries no committed work
    /// beyond the base (an interrupted *initial* implement transitions Ready->InProgress before the
    /// worker commits), the orphaned branch/worktree are pruned and the ticket is reset to Ready so a
    /// clean implement runs - crucially, in a parent chain this lets the branch be recreated inside the
    /// shared worktree rather than re-using an orphaned standalone one (the source of the
    /// shared-vs-standalone worktree confusion). A branch with commits is real in-progress work and is
    /// resumed in place via the rework path.
    /// </summary>
    private async Task<ChainEntry> ResolveInProgressAsync(Ticket ticket, string chainSessionId, CancellationToken ct)
    {
        var names = PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, _workingDirectory);

        int commitsOnBranch = 0;
        try
        {
            var (baseRef, _) = await BaseRefResolver.ResolveAsync(_git, _workingDirectory, _baseOptions.TargetBranch, ct)
                .ConfigureAwait(false);
            commitsOnBranch = await _git.RevListCountAsync($"{baseRef}..{names.BranchName}", _workingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch { /* best-effort: a git failure (e.g. branch absent) is treated as no commits */ }

        if (commitsOnBranch == 0)
        {
            await PruneOrphanBranchAsync(names.BranchName, ct).ConfigureAwait(false);
            await _ticketing.TransitionAsync(ticket.Id, TicketState.Ready, ct).ConfigureAwait(false);
            await EmitResumeTransitionAsync(chainSessionId, ticket.Id, "InProgress", "Ready", ct).ConfigureAwait(false);
            return new ChainEntry(StartPhase.Implement, null, 0);
        }

        // Resume rework in place. Recover the last Rework verdict from the event log if present,
        // else synthesize a neutral resume note (an interrupted implement may never have been reviewed).
        var recovered = _feedbackRetriever?.GetLatestRework(ticket.Id);
        var feedback = recovered is not null
            ? recovered with { ReworkRoundNumber = 1 }
            : new ReviewFeedback(
                "Resume interrupted implementation: a prior implement round for this ticket did not finish. " +
                "Continue or redo the implementation from the current worktree state.",
                Array.Empty<string>(),
                1);
        return new ChainEntry(StartPhase.ResumeImplement, feedback, 1);
    }

    /// <summary>
    /// Removes an orphaned ticket branch and its worktree (if any) so a fresh implement can recreate
    /// the branch without a "branch already exists" collision. Best-effort: a worktree/branch that
    /// cannot be removed is left for the implement phase to surface.
    /// </summary>
    private async Task PruneOrphanBranchAsync(string branchName, CancellationToken ct)
    {
        try
        {
            var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
            foreach (var w in worktrees)
            {
                if (string.Equals(w.Branch, branchName, StringComparison.OrdinalIgnoreCase))
                {
                    await _git.RemoveWorktreeAsync(w.Path, force: true, ct).ConfigureAwait(false);
                    break;
                }
            }
            await _git.DeleteBranchAsync(branchName, force: true, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    private async Task EmitResumeTransitionAsync(string chainSessionId, string ticketId, string from, string to, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.StateTransition,
            TicketId: ticketId,
            Phase: Phase.Chain,
            Data: new Dictionary<string, object>
            {
                ["from"] = from,
                ["to"] = to,
                ["reason"] = "chain_resume"
            }), ct).ConfigureAwait(false);
    }

    private sealed record ChainEntry(StartPhase StartPhase, ReviewFeedback? ResumeFeedback, int ResumeStartRound);

    private enum StartPhase { Plan, Implement, ResumeImplement, Review, Refused }
}
