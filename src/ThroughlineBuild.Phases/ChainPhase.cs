using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Represents the batch implement group for a parent chain.
/// Either all eligible children (auto-discovered at runtime in dependency/numeric order)
/// or an explicit operator-supplied list.
/// </summary>
public abstract record ChainBatchImplementGroup
{
    /// <summary>
    /// Batch all eligible direct children, discovered at runtime in dependency/numeric order.
    /// Used when --batch-implement is specified without a ticket list.
    /// </summary>
    public sealed record AllEligibleChildren : ChainBatchImplementGroup;

    /// <summary>
    /// Batch exactly the listed ticket IDs in the specified order.
    /// Used when --batch-implement is specified with an explicit comma-separated list.
    /// </summary>
    public sealed record ExplicitList(IReadOnlyList<string> TicketIds) : ChainBatchImplementGroup;
}

public record ChainPhaseOptions(
    string TicketId,
    bool Debug,
    Action<string, ChainStep>? OnStep = null,
    bool NoAutoResolve = false,
    string? SharedWorktreePath = null,
    ChainCommitRange? ChainCommitRange = null,
    ChainBatchImplementGroup? BatchImplementGroup = null,
    bool DryRun = false,
    int MaxDepth = 16,
    int Depth = 0,
    IReadOnlySet<string>? VisitedTicketUuids = null,
    string? ChainTargetBranch = null,
    // Absolute path of the parent's integration worktree (the one checked out on
    // ChainTargetBranch). A leaf child's ship advances that branch, so the ship must run
    // here - NOT in the main worktree, which stays parked on the configured root branch.
    // Distinct from SharedWorktreePath, which ImplementPhase reads as "build the ticket
    // inside this worktree"; the integration worktree must not be reused for that.
    string? ChainIntegrationWorktreePath = null,
    // Provides accumulated from all previously shipped upstream tickets in the same chain.
    // Passed through to the gate so the consumes-provides preflight can check whether the
    // current ticket's declared consumes are satisfied. Null means no chain context (single
    // ticket) and is treated identically to an empty set.
    IReadOnlySet<string>? AccumulatedUpstreamProvides = null);

public class ChainPhase
{
    private const int MaxReworkRounds = 2;
    private static readonly object DebugIndexLock = new();

    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> _implementFactory;
    private readonly Func<BuildOptions, GateOutcome?, ReviewPhase> _reviewFactory;
    private readonly Func<BuildOptions, GatePhase>? _gateFactory;
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
    // Root-chain landing: the remote and push toggle used when the OUTERMOST chain
    // fast-forwards its accumulated integration branch onto the configured target branch.
    // A null remote or PushEnabled=false lands locally only (no push) - safe, but the
    // operator must push the target manually.
    private readonly string? _landingRemote;
    private readonly bool _landingPushEnabled;

    public ChainPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions baseOptions,
        Func<BuildOptions, PlanPhase> planFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<string>? sessionIdGenerator = null,
        string? workingDirectory = null,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory = null,
        Func<BuildOptions, ShipPhase>? chainShipFactory = null,
        IGitClient? gitClient = null,
        IReviewFeedbackRetriever? feedbackRetriever = null,
        IWorkerAgent? batchWorker = null,
        string? landingRemote = null,
        bool landingPushEnabled = false,
        Func<BuildOptions, GatePhase>? gateFactory = null)
    {
        _ticketing = ticketing;
        _events = events;
        _baseOptions = baseOptions;
        _planFactory = planFactory;
        _implementFactory = implementFactory;
        _reviewFactory = reviewFactory;
        _gateFactory = gateFactory;
        _shipFactory = shipFactory;
        _chainShipFactory = chainShipFactory;
        _sessionIdGenerator = sessionIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _ratifierFactory = ratifierFactory;
        _git = gitClient ?? new ProcessGitClient();
        _feedbackRetriever = feedbackRetriever;
        _batchWorker = batchWorker;
        _landingRemote = landingRemote;
        _landingPushEnabled = landingPushEnabled;
    }

    // Inert read-only accessors over the wired collaborators, for composition-root tests that
    // verify the chain verb did not silently drop a dependency when constructing this phase.
    // The original --batch-implement bug was exactly a dropped ctor argument (_batchWorker left
    // null), so these let a test fail when that recurs. No runtime behavior depends on them.
    internal IWorkerAgent? BatchWorker => _batchWorker;
    internal Func<BuildOptions, ShipPhase>? ChainShipFactory => _chainShipFactory;

    public async Task<ChainResult> RunAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var steps = new List<ChainStep>();

        var chainSessionId = _sessionIdGenerator();

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        // Preflight hygiene gate: run ONCE at the outermost chain entry (children recurse
        // with ChainTargetBranch set, so they are skipped here). The stash stack is
        // repo-global and leaks across worktrees, so a dangling stash or conflict left on
        // the tree can corrupt a ticket mid-chain. Catch it here - before any planning -
        // instead of burning a plan round and failing opaquely inside implement.
        if (options.ChainTargetBranch is null)
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

        if (options.VisitedTicketUuids is not null && options.VisitedTicketUuids.Contains(ticket.Uuid))
        {
            totalSw.Stop();
            return new ChainResult(
                options.TicketId,
                steps,
                ChainOutcome.ParentStoppedEarly,
                totalSw.Elapsed,
                $"Cycle detected while traversing ticket tree at {options.TicketId}.");
        }

        if (options.DryRun)
        {
            var plan = await BuildDryRunPlanAsync(ticket, options.MaxDepth, ct).ConfigureAwait(false);
            PrintDryRunPlan(plan, options.MaxDepth);
            totalSw.Stop();
            return new ChainResult(
                options.TicketId,
                steps,
                ChainOutcome.DryRunPreview,
                totalSw.Elapsed,
                "Dry-run only; no phases were executed.",
                ChildResults: plan.PostOrder
                    .Select(item => new ChainResult(
                        item.Ticket.Id,
                        Array.Empty<ChainStep>(),
                        ChainOutcome.DryRunPreview,
                        TimeSpan.Zero,
                        item.HasLiveChildren ? "Internal node preview." : "Leaf preview."))
                    .ToList()
                    .AsReadOnly());
        }

        // Parent-ticket chain path: recurse to non-terminal children
        var chainChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (chainChildren.Count > 0)
        {
            if (options.Depth >= options.MaxDepth)
            {
                totalSw.Stop();
                return new ChainResult(
                    options.TicketId,
                    steps,
                    ChainOutcome.ParentStoppedEarly,
                    totalSw.Elapsed,
                    $"Depth cap {options.MaxDepth} reached at {options.TicketId}.");
            }

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
            var buildOpts = BuildPhaseOptions(sessionId, options.TicketId, "plan", targetBranch: options.ChainTargetBranch);
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

        IReadOnlyList<string>? shippedProvides = null;

        if (startPhase == StartPhase.Plan || startPhase == StartPhase.Implement || startPhase == StartPhase.ResumeImplement)
        {
            // ResumeImplement re-enters the loop as a rework round (carries recovered/synthesized
            // feedback at round >= 1), so ImplementPhase reuses the in-progress worktree instead of
            // creating a fresh one. Plan/Implement start a clean initial round.
            var startRound = startPhase == StartPhase.ResumeImplement ? entry.ResumeStartRound : 0;
            var initialFeedback = startPhase == StartPhase.ResumeImplement ? entry.ResumeFeedback : null;
            var (loopFailure, loopProvides) = await RunImplementReviewLoopAsync(options, steps, chainSessionId, startRound, initialFeedback, totalSw, ct)
                .ConfigureAwait(false);
            if (loopFailure is not null)
            {
                totalSw.Stop();
                var finalResult = loopFailure with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
            shippedProvides = loopProvides;
        }
        else
        {
            var (loopFailure, loopProvides) = await RunReviewBranchAsync(options, steps, chainSessionId, 0, totalSw, ct)
                .ConfigureAwait(false);
            if (loopFailure is not null)
            {
                totalSw.Stop();
                var finalResult = loopFailure with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
            shippedProvides = loopProvides;
        }

        var shipSessionId = _sessionIdGenerator();
        var shipBuildOpts = BuildPhaseOptions(shipSessionId, options.TicketId, "ship", targetBranch: options.ChainTargetBranch);
        EmitPhaseStart(options, "ship", -1, shipSessionId);
        var shipSw = Stopwatch.StartNew();
        // When running inside a parent-chain integration branch, use the chain ship factory
        // when supplied. The factory honors BuildOptions.TargetBranch, so the leaf ships into
        // the current integration branch rather than the configured root.
        var useChainShip = options.ChainTargetBranch is not null && _chainShipFactory is not null;
        var activeShipFactory = useChainShip ? _chainShipFactory! : _shipFactory;
        // A leaf in a parent chain ships into its parent's integration branch, which is
        // checked out in the integration worktree - not the main worktree, which stays on
        // the configured root branch. ShipPhase fast-forwards whatever branch is checked out
        // in the directory it is handed and refuses if that is not the target, so the ship
        // must run in the integration worktree. The worktree choice is tied to useChainShip so
        // the ship's target branch and its worktree never disagree. Outside a chain it falls
        // back to the main worktree, where the configured target IS checked out.
        var shipWorkingDirectory = (useChainShip && options.ChainIntegrationWorktreePath is not null)
            ? options.ChainIntegrationWorktreePath
            : _workingDirectory;
        var shipResult = await activeShipFactory(shipBuildOpts).RunAsync(options.TicketId, shipWorkingDirectory, ct)
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

        var completed = new ChainResult(options.TicketId, steps, ChainOutcome.Completed, totalSw.Elapsed, null,
            ShippedProvides: shippedProvides);
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

    private async Task<(ChainResult? abort, IReadOnlyList<string>? successProvides)> RunImplementReviewLoopAsync(
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

        // Gate cost ledger accumulators: track gate wall time and gate-attributable rework tokens
        // across all iterations so a single CostLedger event is emitted per ticket at exit.
        long gateWallMs = 0;
        int gateAttributableReworkRounds = 0;
        long gateAttributableReworkInputTokens = 0;
        long gateAttributableReworkOutputTokens = 0;
        bool gateAttributableReworkTokensTracked = false;
        bool thisRoundIsGateAttributable = false;
        bool gateWasEngaged = false;

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = BuildPhaseOptions(implSessionId, options.TicketId, "implement", round, options.ChainTargetBranch);
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

            // Accumulate gate-attributable rework tokens if this implement round was triggered
            // by a gate hard-fail (identified by the flag set in the prior gate-failure branch).
            if (thisRoundIsGateAttributable)
            {
                gateAttributableReworkRounds++;
                var inp = implResult.LlmInputTokens ?? 0L;
                var outp = implResult.LlmOutputTokens ?? 0L;
                gateAttributableReworkInputTokens += inp;
                gateAttributableReworkOutputTokens += outp;
                if (inp + outp > 0) gateAttributableReworkTokensTracked = true;
                thisRoundIsGateAttributable = false;
            }

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
                        return (new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence), null);
                    }
                    // Ratifier rejected - fall through to StoppedAtImplement
                }
                if (gateWasEngaged)
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtImplement,
                    TimeSpan.Zero, null), null);
            }

            // Gate phase: run checks once on the warm worktree and collect smoke signals.
            // Only engaged when a gate factory was supplied; nil factory skips gate entirely.
            GateOutcome? gateOutcome = null;
            if (_gateFactory is not null)
            {
                var gateSessionId = _sessionIdGenerator();
                var gateBuildOpts = BuildPhaseOptions(gateSessionId, options.TicketId, "gate", round, options.ChainTargetBranch);
                EmitPhaseStart(options, "gate", round, gateSessionId);
                var gateSw = Stopwatch.StartNew();

                var gateWorktreePath = implResult.WorktreePath ?? _workingDirectory;
                var gateBranchName = implResult.BranchName ?? PhaseWorktreeLayout.BranchName(options.TicketId);

                string gateBaseRef;
                try
                {
                    (gateBaseRef, _) = await BaseRefResolver.ResolveAsync(
                        _git, _workingDirectory, _baseOptions.TargetBranch, ct).ConfigureAwait(false);
                }
                catch
                {
                    gateBaseRef = _baseOptions.TargetBranch;
                }

                gateOutcome = await _gateFactory(gateBuildOpts).RunAsync(
                    options.TicketId, gateWorktreePath, gateBranchName, gateBaseRef,
                    _workingDirectory, implResult.CompletionClaim, ct,
                    options.AccumulatedUpstreamProvides).ConfigureAwait(false);
                gateSw.Stop();

                var gateStep = new ChainStep(
                    PhaseName: "gate",
                    ReworkRoundNumber: round,
                    Status: gateOutcome.Passed ? Status.Ok : Status.Failed,
                    FailureReason: gateOutcome.HardFailReason,
                    Verdict: null,
                    Duration: gateSw.Elapsed,
                    PhaseSessionId: gateSessionId);
                steps.Add(gateStep);
                options.OnStep?.Invoke(options.TicketId, gateStep);

                gateWasEngaged = true;
                gateWallMs += gateOutcome.CheckResults.Sum(r => (long)r.Elapsed.TotalMilliseconds);

                if (!gateOutcome.Passed)
                {
                    // Gate already transitioned InReview -> InProgress. Re-enter the rework loop
                    // with the gate failure as feedback so the next implement knows what broke.
                    var gatingFailedResults = gateOutcome.CheckResults
                        .Where(r => r.Role == CheckRole.Gating && !r.Passed && !r.Skipped)
                        .ToList();
                    var gatingFailed = gatingFailedResults.Select(r => r.Name).ToList();
                    var gateRationale = gateOutcome.HardFailReason ?? "gate: gating checks failed";

                    if (round < MaxReworkRounds)
                    {
                        feedback = new ReviewFeedback(gateRationale, gatingFailed, round + 1,
                            GateFailedChecks: gatingFailedResults);
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: chainSessionId,
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.ReworkRound,
                            TicketId: options.TicketId,
                            Phase: Phase.Implement,
                            Data: new Dictionary<string, object>
                            {
                                ["round"] = round + 1,
                                ["verdict_that_triggered"] = "GateFailure",
                                ["rationale_preview"] = RationalePreview(gateRationale) ?? ""
                            }), ct).ConfigureAwait(false);
                        thisRoundIsGateAttributable = true;
                        round++;
                        continue;
                    }
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                    return (new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                        TimeSpan.Zero, gateRationale), null);
                }
            }

            var reviewResult = await RunOneReviewAsync(options, steps, round, gateOutcome, ct).ConfigureAwait(false);

            if (reviewResult.abort is not null)
            {
                if (gateWasEngaged)
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (reviewResult.abort, null);
            }

            var rv = reviewResult.verdict!;

            if (rv.Kind == VerdictKind.Pass)
            {
                if (gateWasEngaged)
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (null, implResult.CompletionClaim?.Provides);
            }

            if (rv.Kind == VerdictKind.Fail)
            {
                if (gateWasEngaged)
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                    TimeSpan.Zero, rv.Rationale), null);
            }

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
                if (gateWasEngaged)
                    await EmitCostLedgerAsync(chainSessionId, options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                    TimeSpan.Zero, rv.Rationale), null);
            }
        }
    }

    private async Task<(ChainResult? abort, IReadOnlyList<string>? successProvides)> RunReviewBranchAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int round,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        // No gate for tickets already in InReview (resume path): ReviewPhase runs checks itself.
        var reviewResult = await RunOneReviewAsync(options, steps, round, null, ct).ConfigureAwait(false);

        if (reviewResult.abort is not null)
            return (reviewResult.abort, null);

        var rv = reviewResult.verdict!;

        if (rv.Kind == VerdictKind.Pass)
            return (null, null);

        if (rv.Kind == VerdictKind.Fail)
            return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, rv.Rationale), null);

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

    // Emits a CostLedger event capturing gate wall time, gate-attributable rework token counts,
    // and annotatable slots (cascade_caught, false_fails) for post-run analysis.
    private async Task EmitCostLedgerAsync(
        string chainSessionId,
        string ticketId,
        long gateWallMs,
        int gateAttributableReworkRounds,
        long gateAttributableReworkInputTokens,
        long gateAttributableReworkOutputTokens,
        bool gateAttributableReworkTokensTracked,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object>
        {
            ["gate_wall_ms"] = gateWallMs,
            ["gate_attributable_rework_rounds"] = gateAttributableReworkRounds,
            ["cascade_caught"] = 0,
            ["false_fails"] = 0
        };
        if (gateAttributableReworkRounds > 0 && gateAttributableReworkTokensTracked)
        {
            data["gate_attributable_rework_input_tokens"] = gateAttributableReworkInputTokens;
            data["gate_attributable_rework_output_tokens"] = gateAttributableReworkOutputTokens;
        }
        else if (gateAttributableReworkRounds > 0)
        {
            data["gate_attributable_rework_tokens_available"] = false;
        }
        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.CostLedger,
            TicketId: ticketId,
            Phase: Phase.Gate,
            Data: data), ct).ConfigureAwait(false);
    }

    private async Task<(ChainResult? abort, Verdict? verdict)> RunOneReviewAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        int round,
        GateOutcome? gateOutcome,
        CancellationToken ct)
    {
        var revSessionId = _sessionIdGenerator();
        // Pass the chain integration branch so the leaf review diffs against the accumulating
        // chain branch (e.g. chain/{parent}), not the root target. ReviewPhase resolves its diff
        // base from TargetBranch; without this a stacked chain hands the reviewer the entire
        // accumulated stack diff instead of just this ticket's own commit. In the non-chain path
        // ChainTargetBranch is null, so this falls back to the root target exactly as before.
        var revBuildOpts = BuildPhaseOptions(revSessionId, options.TicketId, "review", round, options.ChainTargetBranch);
        EmitPhaseStart(options, "review", -1, revSessionId);
        var revSw = Stopwatch.StartNew();
        var revResult = await _reviewFactory(revBuildOpts, gateOutcome)
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

    private BuildOptions BuildPhaseOptions(string sessionId, string ticketId, string phaseName, int? round = null, string? targetBranch = null)
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
                DebugCaptureDirectory = debugCaptureDirectory,
                TargetBranch = targetBranch ?? _baseOptions.TargetBranch
            };
        }

        return _baseOptions with
        {
            SessionId = sessionId,
            DebugCaptureDirectory = debugCaptureDirectory,
            ProgressDigestSink = new PrefixedTextWriter($"[{ticketId}] ", _baseOptions.ProgressDigestSink),
            TargetBranch = targetBranch ?? _baseOptions.TargetBranch
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

    // Wraps the per-ticket ChainResults from a batch implement session alongside the
    // verified commit attributions and branch metadata needed to run a combined review.
    // ConfirmedTickets is null when the session failed before any commits were verified.
    private sealed record BatchImplementOutcome(
        IReadOnlyList<ChainResult> Results,
        IReadOnlyList<BatchTicketResult>? ConfirmedTickets,
        string BranchName,
        string BaseRef);

    /// <summary>
    /// Runs a single implement session for all tickets in the batch group, then returns one
    /// <see cref="ChainResult"/> per ticket with outcome <see cref="ChainOutcome.BatchImplemented"/>.
    /// The session executes inside the already-created shared chain worktree. All batch commits
    /// stack on the first ticket's branch; the combined review runs after this returns.
    /// </summary>
    private async Task<BatchImplementOutcome> RunBatchImplementSessionAsync(
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
            return new BatchImplementOutcome(new[] { branchFail }, null, batchBranchName, baseRef);
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

        // Worker failed: if some tickets were committed before the failure, advance the
        // confirmed ones and leave the first incomplete ticket InProgress so the batch
        // leaves a clean, recoverable boundary. An empty tickets array (or Escalate)
        // means nothing committed; stop all tickets.
        if (workerResult.Status == Status.Failed || workerResult.Status == Status.Escalate)
        {
            var failureReason = workerResult.FailureReason ?? workerResult.Summary;

            // Partial failure path: worker committed some tickets before stopping.
            if (workerResult.Status == Status.Failed &&
                workerResult.Tickets is not null && workerResult.Tickets.Count > 0)
            {
                var partialBase = !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef;
                var partialVerify = await BatchCommitVerifier
                    .VerifyAsync(_git, sharedWorktreePath, partialBase, workerResult.Tickets, ct)
                    .ConfigureAwait(false);

                if (partialVerify.Success)
                {
                    var confirmedIds = new HashSet<string>(
                        partialVerify.ConfirmedTickets.Select(r => r.TicketId),
                        StringComparer.Ordinal);

                    // Advance each confirmed ticket: post implemented_at marker + InProgress->InReview.
                    foreach (var confirmedTicket in partialVerify.ConfirmedTickets)
                    {
                        var batchTicket = batchTickets.FirstOrDefault(
                            t => string.Equals(t.Id, confirmedTicket.TicketId, StringComparison.Ordinal));
                        if (batchTicket is null) continue;

                        string summaryHtml = "";
                        if (workerResult.Blocks is not null &&
                            !string.IsNullOrEmpty(confirmedTicket.SummaryRef) &&
                            workerResult.Blocks.TryGetValue(confirmedTicket.SummaryRef, out var summaryMd) &&
                            !string.IsNullOrEmpty(summaryMd))
                        {
                            summaryHtml = MarkdownRenderer.Render(summaryMd);
                        }

                        var markerHtml =
                            $"<p>[implemented_at: {confirmedTicket.CommitSha}] (branch {batchBranchName})" +
                            $" (batch: stack_position={confirmedTicket.StackPosition})</p>{summaryHtml}";
                        try
                        {
                            await _ticketing.CreateCommentAsync(batchTicket.Id, markerHtml, ct).ConfigureAwait(false);
                        }
                        catch { /* non-fatal */ }

                        try
                        {
                            await _ticketing.TransitionAsync(batchTicket.Id, TicketState.InReview, ct).ConfigureAwait(false);
                        }
                        catch { /* non-fatal */ }
                    }

                    // Post failure reason to the first incomplete ticket so the operator
                    // can see why the batch stopped without consulting the event log.
                    var firstIncomplete = batchTickets.FirstOrDefault(t => !confirmedIds.Contains(t.Id));
                    if (firstIncomplete is not null)
                    {
                        try
                        {
                            var failHtml = $"<p>batch implement stopped: {WebUtility.HtmlEncode(failureReason)}</p>";
                            await _ticketing.CreateCommentAsync(firstIncomplete.Id, failHtml, ct).ConfigureAwait(false);
                        }
                        catch { /* non-fatal */ }
                    }

                    // Return mixed results: confirmed tickets -> BatchImplemented,
                    // incomplete tickets -> StoppedAtImplement.
                    batchSw.Stop();
                    var partialResults = new List<ChainResult>(batchTickets.Count);
                    for (int i = 0; i < batchTickets.Count; i++)
                    {
                        var ticket = batchTickets[i];
                        var perResult = partialVerify.ConfirmedTickets.FirstOrDefault(
                            r => string.Equals(r.TicketId, ticket.Id, StringComparison.Ordinal));

                        if (perResult is not null)
                        {
                            var implStep = new ChainStep(
                                PhaseName: "batch-implement",
                                ReworkRoundNumber: 0,
                                Status: Status.Ok,
                                FailureReason: null,
                                Verdict: null,
                                Duration: implSw.Elapsed,
                                PhaseSessionId: batchSessionId);
                            partialResults.Add(new ChainResult(
                                TicketId: ticket.Id,
                                Steps: new[] { implStep },
                                Outcome: ChainOutcome.BatchImplemented,
                                TotalDuration: batchSw.Elapsed,
                                FinalRationale: $"batch implement succeeded; commit {perResult.CommitSha}"));
                        }
                        else
                        {
                            partialResults.Add(new ChainResult(
                                ticket.Id, Array.Empty<ChainStep>(),
                                ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                                failureReason));
                        }
                    }
                    return new BatchImplementOutcome(
                        partialResults.AsReadOnly(), null, batchBranchName,
                        !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef);
                }
                // Partial verification failed: fall through to total failure path.
            }

            batchSw.Stop();
            return new BatchImplementOutcome(
                batchTickets.Select(t => new ChainResult(
                    t.Id, Array.Empty<ChainStep>(),
                    ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                    failureReason)).ToList().AsReadOnly(),
                null, batchBranchName, baseRef);
        }

        // Worker succeeded: confirm the worktree is clean and that each reported SHA
        // exists in the branch in declared stack order. Fail before any marker is posted
        // when the check does not hold, naming the first ticket that fails.
        var verifyBase = !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef;

        // Prefer the worker's per-ticket self-report; if it omitted the tickets array,
        // reconstruct attribution from the actual commits. The brief mandates one commit
        // per ticket in declared stack order, so the commits map 1:1 onto the tickets -
        // git is the source of truth and a forgotten echo must not discard real work.
        var reportedTickets = workerResult.Tickets;
        if (reportedTickets is null || reportedTickets.Count == 0)
        {
            var recon = await BatchCommitVerifier
                .ReconstructFromGitAsync(
                    _git, sharedWorktreePath, verifyBase,
                    batchTickets.Select(t => t.Id).ToList(), ct)
                .ConfigureAwait(false);
            if (!recon.Success)
            {
                batchSw.Stop();
                return new BatchImplementOutcome(
                    batchTickets.Select(t => new ChainResult(
                        t.Id, Array.Empty<ChainStep>(),
                        ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                        recon.FailureReason)).ToList().AsReadOnly(),
                    null, batchBranchName, verifyBase);
            }
            reportedTickets = recon.ConfirmedTickets;
        }

        var verifyResult = await BatchCommitVerifier
            .VerifyAsync(_git, sharedWorktreePath, verifyBase, reportedTickets, ct)
            .ConfigureAwait(false);
        if (!verifyResult.Success)
        {
            batchSw.Stop();
            return new BatchImplementOutcome(
                batchTickets.Select(t => new ChainResult(
                    t.Id, Array.Empty<ChainStep>(),
                    ChainOutcome.StoppedAtImplement, batchSw.Elapsed,
                    verifyResult.FailureReason)).ToList().AsReadOnly(),
                null, batchBranchName, !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef);
        }

        // Produce a BatchImplemented result for each ticket, sourcing per-ticket commit
        // attribution from the confirmed git state rather than the worker's self-report.
        // For each confirmed ticket, post the implemented_at marker and transition to InReview
        // so downstream review and ship read the batch stack through the same markers and states
        // as a single-ticket run.
        var perTicketResults = verifyResult.ConfirmedTickets;
        var results = new List<ChainResult>(batchTickets.Count);
        for (int i = 0; i < batchTickets.Count; i++)
        {
            var ticket = batchTickets[i];
            var perTicket = perTicketResults.FirstOrDefault(
                r => string.Equals(r.TicketId, ticket.Id, StringComparison.Ordinal));

            if (perTicket is null)
            {
                // No commit attribution for this ticket: without a SHA we cannot post the
                // implemented_at marker, transition to InReview, or ship it. Stop it
                // explicitly rather than laundering it into a BatchImplemented success that
                // never advances - that silent half-complete is what made a fully-committed
                // batch read as "child did not complete" to the parent chain.
                results.Add(new ChainResult(
                    TicketId: ticket.Id,
                    Steps: Array.Empty<ChainStep>(),
                    Outcome: ChainOutcome.StoppedAtImplement,
                    TotalDuration: batchSw.Elapsed,
                    FinalRationale:
                        $"batch implement: no commit attribution for {ticket.Id}; the worker " +
                        "self-report omitted it and it could not be reconstructed from git"));
                continue;
            }

            // Resolve per-ticket summary markdown from the worker's fenced blocks (best-effort).
            string summaryHtml = "";
            if (workerResult.Blocks is not null &&
                !string.IsNullOrEmpty(perTicket.SummaryRef) &&
                workerResult.Blocks.TryGetValue(perTicket.SummaryRef, out var summaryMarkdown) &&
                !string.IsNullOrEmpty(summaryMarkdown))
            {
                summaryHtml = MarkdownRenderer.Render(summaryMarkdown);
            }

            // Post the implemented_at marker in the same shape as a single-ticket run.
            // Batch fields use parens (not brackets) so the marker parser does not read
            // them as additional markers.
            var commentHtml =
                $"<p>[implemented_at: {perTicket.CommitSha}] (branch {batchBranchName})" +
                $" (batch: stack_position={perTicket.StackPosition})</p>{summaryHtml}";
            try
            {
                await _ticketing.CreateCommentAsync(ticket.Id, commentHtml, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: marker posting failure must not block the batch result */ }

            // Transition InProgress -> InReview to match single-ticket run observable state.
            try
            {
                await _ticketing.TransitionAsync(ticket.Id, TicketState.InReview, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: transition failure must not block the batch result */ }

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
                FinalRationale: $"batch implement succeeded; commit {perTicket.CommitSha}"));
        }

        return new BatchImplementOutcome(
            results.AsReadOnly(),
            verifyResult.ConfirmedTickets,
            batchBranchName,
            !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef);
    }

    // Outcome returned by RunCombinedBatchReviewAsync so callers can distinguish
    // Pass / Rework / Fail and read the feedback without re-parsing worker metadata.
    private sealed record BatchReviewOutcome(
        bool Passed,
        VerdictKind FinalVerdict,
        string Rationale,
        IReadOnlyList<string> ChecksFailed);

    // Routing classification for batch review rework feedback.
    // Localized: feedback names exactly one ticket -> per-ticket rework on that ticket.
    // CrossTicket: feedback spans the group -> re-enter batch context.
    internal enum BatchReworkRoute { Localized, CrossTicket }

    /// <summary>
    /// Runs one combined review pass over the full batch stack diff. When the first pass
    /// returns Rework, or when the batch size exceeds
    /// <see cref="BuildOptions.BatchReviewSizeThreshold"/>, a second pass is run for
    /// additional scrutiny. Returns the final verdict outcome.
    /// </summary>
    private async Task<BatchReviewOutcome> RunCombinedBatchReviewAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string batchBranchName,
        string baseRef,
        string sharedWorktreePath,
        string chainSessionId,
        CancellationToken ct)
    {
        var primaryTicketId = batchTickets[0].Id;
        bool sizeExceedsThreshold = batchTickets.Count > _baseOptions.BatchReviewSizeThreshold;

        // Compute the combined diff once - both passes use the same combined diff since
        // the worktree does not change between review passes.
        GitDiff combinedDiff;
        try
        {
            combinedDiff = await _git
                .DiffAsync(baseRef, batchBranchName, _workingDirectory, includePatchContent: true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _events.EmitAsync(new WorkflowEvent(
                SessionId: chainSessionId,
                Timestamp: DateTimeOffset.UtcNow,
                Kind: EventKind.GateFailure,
                TicketId: primaryTicketId,
                Phase: Phase.Review,
                Data: new Dictionary<string, object>
                {
                    ["kind"] = "batch_review_diff_failed",
                    ["error"] = ex.Message
                }), ct).ConfigureAwait(false);
            return new BatchReviewOutcome(false, VerdictKind.Fail,
                $"diff failed: {ex.Message}", Array.Empty<string>());
        }

        // Pass 1
        var (pass1Verdict, pass1Rationale, pass1Checks) = await RunOneBatchReviewPassAsync(
            batchTickets, confirmedTickets, baseRef, combinedDiff,
            sharedWorktreePath, chainSessionId, pass: 1, ct).ConfigureAwait(false);

        await PostBatchReviewCommentAsync(
            primaryTicketId, 1, pass1Verdict, pass1Rationale, pass1Checks, ct).ConfigureAwait(false);

        // Fail: no second pass - a Fail verdict is a structural problem that a second
        // review pass cannot change.
        if (pass1Verdict == VerdictKind.Fail)
            return new BatchReviewOutcome(false, VerdictKind.Fail, pass1Rationale, pass1Checks);

        // Second pass: triggered when first returned Rework, or the batch exceeds the size
        // threshold (large diffs warrant more than one reviewer pass regardless of verdict).
        bool needSecondPass = pass1Verdict == VerdictKind.Rework || sizeExceedsThreshold;
        if (!needSecondPass)
            return new BatchReviewOutcome(true, VerdictKind.Pass, pass1Rationale, pass1Checks);

        var (pass2Verdict, pass2Rationale, pass2Checks) = await RunOneBatchReviewPassAsync(
            batchTickets, confirmedTickets, baseRef, combinedDiff,
            sharedWorktreePath, chainSessionId, pass: 2, ct).ConfigureAwait(false);

        await PostBatchReviewCommentAsync(
            primaryTicketId, 2, pass2Verdict, pass2Rationale, pass2Checks, ct).ConfigureAwait(false);

        return new BatchReviewOutcome(
            pass2Verdict == VerdictKind.Pass,
            pass2Verdict,
            pass2Rationale,
            pass2Checks);
    }

    /// <summary>
    /// Dispatches the batch review worker for one pass and parses the resulting verdict.
    /// </summary>
    private async Task<(VerdictKind verdict, string rationale, IReadOnlyList<string> checksFailed)>
        RunOneBatchReviewPassAsync(
            IReadOnlyList<Ticket> batchTickets,
            IReadOnlyList<BatchTicketResult> confirmedTickets,
            string baseRef,
            GitDiff combinedDiff,
            string sharedWorktreePath,
            string chainSessionId,
            int pass,
            CancellationToken ct)
    {
        var primaryTicketId = batchTickets[0].Id;
        var reviewSessionId = _sessionIdGenerator();

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: reviewSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.WorkerSpawn,
            TicketId: primaryTicketId,
            Phase: Phase.Review,
            Data: new Dictionary<string, object>
            {
                ["worker"] = _batchWorker!.Name,
                ["role"] = "batch_verifier",
                ["pass"] = pass
            }), ct).ConfigureAwait(false);

        var reviewBrief = BatchReviewBriefBuilder.Build(
            _batchWorker!.Name,
            batchTickets,
            confirmedTickets,
            baseRef,
            combinedDiff,
            checkResults: Array.Empty<CheckResult>());

        var maxSize = batchTickets.Max(t => WorkerSizeMapper.FromTicketSize(t.Size));
        var workerOptions = new WorkerOptions(
            _baseOptions.WorkerTimeout,
            _baseOptions.WorkerAllowedTools,
            DebugCaptureDirectory: _baseOptions.DebugCaptureDirectory,
            LiveStdoutSink: _baseOptions.LiveStdoutSink,
            LiveStderrSink: _baseOptions.LiveStderrSink,
            ProgressDigestSink: _baseOptions.ProgressDigestSink,
            Size: maxSize);

        // Workers run in the shared worktree (where the batch branch is checked out).
        var workerResult = await _batchWorker!
            .ExecuteAsync(reviewBrief, sharedWorktreePath, workerOptions, ct)
            .ConfigureAwait(false);

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: reviewSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.VerifierVerdict,
            TicketId: primaryTicketId,
            Phase: Phase.Review,
            Data: new Dictionary<string, object>
            {
                ["worker_status"] = workerResult.Status.ToString(),
                ["pass"] = pass
            }), ct).ConfigureAwait(false);

        if (workerResult.Status != Status.Ok)
        {
            var reason = workerResult.FailureReason ?? workerResult.Status.ToString();
            return (VerdictKind.Fail, $"batch review worker failed (pass {pass}): {reason}", Array.Empty<string>());
        }

        var metadata = workerResult.Metadata;
        var verdictRaw = TryGetBatchReviewMetadataString(metadata, "verdict");
        VerdictKind kind;
        if (string.Equals(verdictRaw, "Pass", StringComparison.OrdinalIgnoreCase))
            kind = VerdictKind.Pass;
        else if (string.Equals(verdictRaw, "Rework", StringComparison.OrdinalIgnoreCase))
            kind = VerdictKind.Rework;
        else
            kind = VerdictKind.Fail;

        var blocks = workerResult.Blocks ?? new Dictionary<string, string>();
        string rationale;
        if (FencedBlockResolver.TryResolveRef(blocks, metadata, "rationale_ref", out var resolvedRationale, out _)
            && resolvedRationale is not null)
            rationale = resolvedRationale;
        else
            rationale = TryGetBatchReviewMetadataString(metadata, "rationale") ?? "";

        var checksFailed = ParseBatchReviewChecksFailed(metadata);
        return (kind, rationale, checksFailed);
    }

    private async Task PostBatchReviewCommentAsync(
        string ticketId,
        int pass,
        VerdictKind verdict,
        string rationale,
        IReadOnlyList<string> checksFailed,
        CancellationToken ct)
    {
        try
        {
            var checksNote = checksFailed.Count > 0
                ? $" checks_failed: {string.Join(", ", checksFailed)}"
                : "";
            var passNote = pass > 1 ? $" (pass {pass})" : "";
            var commentHtml =
                $"<p>[batch_review{passNote}: {verdict}]{checksNote}</p>" +
                $"<p>{System.Net.WebUtility.HtmlEncode(rationale)}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    private static string? TryGetBatchReviewMetadataString(
        IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je
            && je.ValueKind == System.Text.Json.JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }

    private static IReadOnlyList<string> ParseBatchReviewChecksFailed(
        IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("checks_failed", out var raw) || raw is null)
            return Array.Empty<string>();
        if (raw is IEnumerable<string> strings)
            return strings.ToArray();
        if (raw is IEnumerable<object> objects)
        {
            var result = new List<string>();
            foreach (var item in objects)
            {
                if (item is string s) result.Add(s);
                else if (item is System.Text.Json.JsonElement je
                    && je.ValueKind == System.Text.Json.JsonValueKind.String)
                    result.Add(je.GetString() ?? "");
            }
            return result;
        }
        if (raw is System.Text.Json.JsonElement arrayElem
            && arrayElem.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var elem in arrayElem.EnumerateArray())
            {
                if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
                    result.Add(elem.GetString() ?? "");
            }
            return result;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Classifies batch review rework feedback as localized (names exactly one batch ticket)
    /// or cross-ticket (names zero or multiple tickets, or relates to integration seams).
    /// </summary>
    /// <summary>
    /// Checks the declared batch group against the configured size caps before any session
    /// starts. Returns a human-readable description of the first exceeded cap (e.g.
    /// "max_tickets=8 (actual 10)"), or null when all caps pass. Three caps are checked
    /// in order: ticket count, aggregate size score (S=1/M=2/L=4), and total description
    /// bytes (a proxy for estimated worker context). The caller logs the returned string and
    /// falls back to the per-ticket chain path.
    /// </summary>
    internal static string? CheckBatchSizeCaps(IReadOnlyList<Ticket> batchTickets, BuildOptions opts)
    {
        if (batchTickets.Count > opts.BatchMaxTickets)
            return $"max_tickets={opts.BatchMaxTickets} (actual {batchTickets.Count})";

        int sizeScore = 0;
        foreach (var t in batchTickets)
        {
            sizeScore += t.Size switch
            {
                Size.S => 1,
                Size.M => 2,
                Size.L => 4,
                _ => 1
            };
        }
        if (sizeScore > opts.BatchMaxSizeScore)
            return $"max_size_score={opts.BatchMaxSizeScore} (actual {sizeScore})";

        int descBytes = 0;
        foreach (var t in batchTickets)
            descBytes += System.Text.Encoding.UTF8.GetByteCount(t.DescriptionHtml ?? string.Empty);
        if (descBytes > opts.BatchMaxDescriptionBytes)
            return $"max_description_bytes={opts.BatchMaxDescriptionBytes} (actual {descBytes})";

        return null;
    }

    internal static BatchReworkRoute ClassifyBatchRework(
        IReadOnlyList<Ticket> batchTickets,
        string rationale)
    {
        int count = 0;
        foreach (var ticket in batchTickets)
        {
            if (rationale.Contains(ticket.Id, StringComparison.Ordinal))
                count++;
            if (count > 1)
                return BatchReworkRoute.CrossTicket;
        }
        return count == 1 ? BatchReworkRoute.Localized : BatchReworkRoute.CrossTicket;
    }

    /// <summary>
    /// Runs the initial combined review and any required rework rounds (up to MaxReworkRounds).
    /// Localized feedback (single ticket named) triggers per-ticket rework; cross-ticket feedback
    /// re-enters batch context. Returns true if the final verdict is Pass.
    /// </summary>
    private async Task<bool> RunBatchReviewAndReworkAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string batchBranchName,
        string baseRef,
        string sharedWorktreePath,
        string? chainStartSha,
        CancellationToken ct)
    {
        var currentConfirmedTickets = confirmedTickets;

        // Initial review
        var outcome = await RunCombinedBatchReviewAsync(
            batchTickets, currentConfirmedTickets, batchBranchName, baseRef,
            sharedWorktreePath, _sessionIdGenerator(), ct).ConfigureAwait(false);

        if (outcome.Passed) return true;
        if (outcome.FinalVerdict == VerdictKind.Fail) return false;

        // Rework loop: up to MaxReworkRounds attempts
        for (int reworkRound = 1; reworkRound <= MaxReworkRounds; reworkRound++)
        {
            var feedback = new ReviewFeedback(outcome.Rationale, outcome.ChecksFailed, reworkRound);
            var route = ClassifyBatchRework(batchTickets, outcome.Rationale);

            if (route == BatchReworkRoute.Localized)
            {
                // Exactly one batch ticket is mentioned: run per-ticket rework on that ticket.
                var targetTicket = batchTickets.First(
                    t => outcome.Rationale.Contains(t.Id, StringComparison.Ordinal));
                var reworkOk = await RunLocalizedBatchReworkAsync(
                    targetTicket.Id, feedback, sharedWorktreePath, reworkRound, ct)
                    .ConfigureAwait(false);
                if (!reworkOk)
                    return false;
            }
            else
            {
                // Feedback spans the group: re-enter batch context with the feedback.
                var newConfirmed = await RunCrossTicketBatchReworkAsync(
                    batchTickets, batchBranchName, baseRef, sharedWorktreePath,
                    chainStartSha, feedback, ct).ConfigureAwait(false);
                if (newConfirmed is null)
                    return false;
                currentConfirmedTickets = newConfirmed;
            }

            // Re-review after rework
            outcome = await RunCombinedBatchReviewAsync(
                batchTickets, currentConfirmedTickets, batchBranchName, baseRef,
                sharedWorktreePath, _sessionIdGenerator(), ct).ConfigureAwait(false);

            if (outcome.Passed) return true;
            if (outcome.FinalVerdict == VerdictKind.Fail) return false;
            // If Rework again and rounds remain, continue the loop
        }

        return false; // MaxReworkRounds exceeded
    }

    /// <summary>
    /// Handles localized batch rework: a single ticket identified by the review feedback is
    /// reworked via the standard per-ticket ImplementPhase path. The rework commit is additive;
    /// it lands on the batch branch and leaves existing marker-bearing commits intact.
    /// </summary>
    private async Task<bool> RunLocalizedBatchReworkAsync(
        string targetTicketId,
        ReviewFeedback feedback,
        string sharedWorktreePath,
        int reworkRound,
        CancellationToken ct)
    {
        // Transition the affected ticket InReview -> InProgress so ImplementPhase's
        // state check passes. ImplementPhase will transition it back to InReview after
        // adding the rework commit and posting a new [implemented_at:] marker.
        try
        {
            await _ticketing.TransitionAsync(targetTicketId, TicketState.InProgress, ct)
                .ConfigureAwait(false);
        }
        catch { /* non-fatal: ImplementPhase will surface the wrong-state error if needed */ }

        var sessionId = _sessionIdGenerator();
        var buildOpts = BuildPhaseOptions(sessionId, targetTicketId, "batch-rework-localized");
        var implPhaseOpts = new ImplementPhaseOptions(
            ReviewFeedback: feedback,
            SharedWorktreePath: sharedWorktreePath);
        var implPhase = _implementFactory(buildOpts, implPhaseOpts);
        var implResult = await implPhase.RunAsync(targetTicketId, _workingDirectory, ct)
            .ConfigureAwait(false);
        return implResult.Success;
    }

    /// <summary>
    /// Handles cross-ticket batch rework: re-runs the batch implement worker with the review
    /// feedback embedded in the brief. Rework commits are additive and land on top of the
    /// existing batch branch. Returns the updated confirmed-ticket list on success, or null
    /// if the rework session failed completely.
    /// </summary>
    private async Task<IReadOnlyList<BatchTicketResult>?> RunCrossTicketBatchReworkAsync(
        IReadOnlyList<Ticket> batchTickets,
        string batchBranchName,
        string baseRef,
        string sharedWorktreePath,
        string? chainStartSha,
        ReviewFeedback feedback,
        CancellationToken ct)
    {
        // Transition all tickets InReview -> InProgress for the rework session.
        foreach (var ticket in batchTickets)
        {
            try
            {
                await _ticketing.TransitionAsync(ticket.Id, TicketState.InProgress, ct)
                    .ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        }

        // Build chain commit range for context (best-effort).
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

        string mainSha;
        try
        {
            mainSha = await _git.RevParseAsync(baseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { mainSha = string.Empty; }

        var topLevelEntries = Directory.EnumerateFileSystemEntries(_workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);

        var reworkSessionId = _sessionIdGenerator();
        var firstTicket = batchTickets[0];
        var batchBuildOpts = BuildPhaseOptions(reworkSessionId, firstTicket.Id, "batch-rework-cross");

        var batchBrief = BatchImplementBriefBuilder.Build(
            _batchWorker!.Name,
            batchTickets,
            repoState,
            batchBranchName,
            sharedWorktreePath,
            batchCommitRange,
            reworkFeedback: feedback);

        var maxSize = batchTickets.Max(t => WorkerSizeMapper.FromTicketSize(t.Size));
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

        // Require at least some committed tickets to continue.
        var reportedTickets = workerResult.Tickets;
        if (reportedTickets is null || reportedTickets.Count == 0)
            return null;

        var verifyBase = !string.IsNullOrEmpty(mainSha) ? mainSha : baseRef;
        var verifyResult = await BatchCommitVerifier
            .VerifyAsync(_git, sharedWorktreePath, verifyBase, reportedTickets, ct)
            .ConfigureAwait(false);

        if (!verifyResult.Success)
            return null;

        // Post new [implemented_at:] markers for the rework commits and transition
        // confirmed tickets back to InReview.
        foreach (var confirmed in verifyResult.ConfirmedTickets)
        {
            var markerHtml =
                $"<p>[implemented_at: {confirmed.CommitSha}] (branch {batchBranchName})" +
                $" (batch-rework: stack_position={confirmed.StackPosition})</p>";
            try
            {
                await _ticketing.CreateCommentAsync(confirmed.TicketId, markerHtml, ct)
                    .ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
            try
            {
                await _ticketing.TransitionAsync(confirmed.TicketId, TicketState.InReview, ct)
                    .ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        }

        return verifyResult.ConfirmedTickets;
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

        // Build a level-based execution schedule from sibling blocked_by relations so that
        // dependent siblings are serialized while independent siblings still run in order.
        var siblingGraph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
        var levels = TopologicalSorter.ComputeLevels(siblingGraph);

        // Print the dependency order derived from Plane before any phase runs so a wrong
        // or missing edge is visible up front. Tickets in the same level have no blocked_by
        // edge between them and are unordered relative to each other (Brief 17).
        PrintDispatchOrder(options.TicketId, levels);

        var integrationBaseRef = options.ChainTargetBranch ?? _baseOptions.TargetBranch;
        var integrationNames = PhaseWorktreeLayout.Compute(parentTicket.Id, parentTicket.Title, _workingDirectory);
        var integrationBranch = ChainIntegrationBranch(parentTicket);
        var integrationWorktreePath = integrationNames.WorktreePath;

        // Capture the target head SHA once at chain start so ChainCommitRangeHelper can
        // later compute the range of commits the chain has produced (Brief 08).
        string? chainStartSha = null;
        try
        {
            chainStartSha = await _git.RevParseAsync(integrationBaseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal: chainStartSha stays null */ }

        string? sharedWorktreePath = null;
        if (eligible.Count > 0)
        {
            var createResult = await EnsureIntegrationWorktreeAsync(
                integrationBranch,
                integrationBaseRef,
                integrationWorktreePath,
                ct).ConfigureAwait(false);
            if (createResult.Success)
            {
                sharedWorktreePath = createResult.AbsolutePath ?? integrationWorktreePath;
            }
            else
            {
                Console.Error.WriteLine(
                    $"[{parentTicket.Id}] integration worktree unavailable " +
                    $"({createResult.FailureReason}); cannot safely accumulate nested chain branches.");
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: _sessionIdGenerator(),
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.GateFailure,
                    TicketId: parentTicket.Id,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["kind"] = "integration_worktree_unavailable",
                        ["detail"] = createResult.FailureReason ?? "unknown",
                        ["branch"] = integrationBranch,
                        ["path"] = integrationWorktreePath
                    }), ct).ConfigureAwait(false);
                totalSw.Stop();
                return new ChainResult(
                    options.TicketId,
                    Array.Empty<ChainStep>(),
                    ChainOutcome.ParentStoppedEarly,
                    totalSw.Elapsed,
                    $"Could not create integration branch {integrationBranch}: {createResult.FailureReason}",
                    ChildResults: Array.Empty<ChainResult>());
            }
        }

        var allChildResults = new List<ChainResult>();
        bool anyStoppedEarly = false;

        HashSet<string>? batchedTicketIds = null;
        if (options.BatchImplementGroup is not null
            && _batchWorker is not null
            && sharedWorktreePath is not null
            && !anyStoppedEarly)
        {
            // Candidate set for the batch. Ready children batch directly; Backlog children are
            // planned first (below) so --batch-implement engages on a freshly scaffolded op - whose
            // children are all Backlog - instead of silently selecting none and running the
            // per-ticket chain. Mid-flight states (Planning/InProgress/InReview) are not batched and
            // fall through to the per-ticket level loop.
            IReadOnlyList<Ticket> batchCandidates;
            if (options.BatchImplementGroup is ChainBatchImplementGroup.AllEligibleChildren)
            {
                batchCandidates = levels
                    .SelectMany(level => level
                        .Select(id => eligible.First(c => string.Equals(c.Id, id, StringComparison.Ordinal))))
                    .Where(t => t.State == TicketState.Ready || t.State == TicketState.Backlog)
                    .ToList();
            }
            else if (options.BatchImplementGroup is ChainBatchImplementGroup.ExplicitList explicitGroup)
            {
                batchCandidates = explicitGroup.TicketIds
                    .Where(id => eligible.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                    .Select(id => eligible.First(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                    .Where(t => t.State == TicketState.Ready || t.State == TicketState.Backlog)
                    .ToList();
            }
            else
            {
                batchCandidates = Array.Empty<Ticket>();
            }

            // Exclude any candidate that is itself an internal node (has its own non-terminal
            // children). Such a ticket is a parent, not a leaf carrying code - batching it would
            // "implement" a parent as if it had a diff. Internal nodes must fall through to the
            // per-child level loop, which recurses them as parents (chaining their own children).
            // Applies equally to AllEligibleChildren and an explicitly listed ticket that turns
            // out to be an internal node. Each skip is logged so the downgrade is never silent.
            if (batchCandidates.Count > 0)
            {
                var leafCandidates = new List<Ticket>(batchCandidates.Count);
                foreach (var candidate in batchCandidates)
                {
                    var grandchildren = await _ticketing
                        .QueryAsync(new TicketQuery(ParentId: candidate.Uuid), ct).ConfigureAwait(false);
                    var hasLiveChildren = grandchildren.Any(
                        g => g.State != TicketState.Done && g.State != TicketState.Cancelled);
                    if (hasLiveChildren)
                    {
                        Console.Error.WriteLine(
                            $"[{parentTicket.Id}] batch-implement: skipping {candidate.Id} - it is an " +
                            "internal node (has non-terminal children); chaining it as a parent instead.");
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: _sessionIdGenerator(),
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.GateFailure,
                            TicketId: parentTicket.Id,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["kind"] = "batch_skip_internal_node",
                                ["ticket"] = candidate.Id
                            }), ct).ConfigureAwait(false);
                        continue;
                    }
                    leafCandidates.Add(candidate);
                }
                batchCandidates = leafCandidates;
            }

            if (batchCandidates.Count > 0)
            {
                // Plan any Backlog candidates up front so the batch implement session has a Ready
                // plan for each (the implement brief reads each ticket's description as its plan).
                // Only the implement->review->ship is batched; planning stays per-ticket. A plan
                // failure stops the chain, mirroring the per-ticket StoppedAtPlan.
                var batchTicketList = new List<Ticket>(batchCandidates.Count);
                bool planStopped = false;
                foreach (var candidate in batchCandidates)
                {
                    if (candidate.State == TicketState.Backlog)
                    {
                        var planReason = await PlanForBatchAsync(options, candidate.Id, ct).ConfigureAwait(false);
                        if (planReason is not null)
                        {
                            allChildResults.Add(new ChainResult(candidate.Id, Array.Empty<ChainStep>(),
                                ChainOutcome.StoppedAtPlan, TimeSpan.Zero, planReason));
                            anyStoppedEarly = true;
                            planStopped = true;
                            break;
                        }
                    }
                    // Re-fetch so the batch implement brief sees the planned description and Ready state.
                    batchTicketList.Add(await _ticketing.GetAsync(candidate.Id, ct).ConfigureAwait(false));
                }

                IReadOnlyList<Ticket> batchTickets = batchTicketList;

                if (!planStopped && batchTickets.Count > 0)
                {
                    var capViolation = CheckBatchSizeCaps(batchTickets, _baseOptions);
                    if (capViolation is not null)
                    {
                        Console.Error.WriteLine(
                            $"[{parentTicket.Id}] batch-size-fallback: cap exceeded ({capViolation}); " +
                            $"running per-ticket chain for all {batchTickets.Count} ticket(s) instead.");
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: _sessionIdGenerator(),
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.GateFailure,
                            TicketId: parentTicket.Id,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["kind"] = "batch_size_cap_exceeded",
                                ["cap"] = capViolation,
                                ["ticket_count"] = batchTickets.Count
                            }), ct).ConfigureAwait(false);
                        // batchedTicketIds stays null: the now-Ready planned tickets fall through to
                        // the per-ticket level loop and resume at implement (no re-plan).
                    }
                    else
                    {
                        batchedTicketIds = new HashSet<string>(
                            batchTickets.Select(t => t.Id), StringComparer.Ordinal);

                        var batchOutcome = await RunBatchImplementSessionAsync(
                            options, batchTickets, sharedWorktreePath, integrationBranch, chainStartSha, ct)
                            .ConfigureAwait(false);

                        foreach (var br in batchOutcome.Results)
                        {
                            allChildResults.Add(br);
                            if (!IsChainSuccess(br.Outcome))
                            {
                                anyStoppedEarly = true;
                                break;
                            }
                        }

                        if (!anyStoppedEarly
                            && _batchWorker is not null
                            && batchOutcome.ConfirmedTickets is not null
                            && batchOutcome.ConfirmedTickets.Count > 0)
                        {
                            var batchReviewPassed = await RunBatchReviewAndReworkAsync(
                                batchTickets,
                                batchOutcome.ConfirmedTickets,
                                batchOutcome.BranchName,
                                batchOutcome.BaseRef,
                                sharedWorktreePath,
                                chainStartSha,
                                ct).ConfigureAwait(false);

                            if (!batchReviewPassed)
                            {
                                anyStoppedEarly = true;
                            }
                            else
                            {
                                // Ship the reviewed batch stack into the integration branch: advance
                                // chain/<parent> to the batch tip and mark each ticket Done. The root
                                // landing then carries it to the target, exactly like a leaf ship.
                                var shipReason = await ShipBatchStackAsync(
                                    batchTickets, batchOutcome.BranchName, integrationBranch,
                                    sharedWorktreePath, ct).ConfigureAwait(false);
                                if (shipReason is not null)
                                {
                                    Console.Error.WriteLine($"[{parentTicket.Id}] batch ship failed: {shipReason}");
                                    anyStoppedEarly = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        else if (options.BatchImplementGroup is not null && !anyStoppedEarly)
        {
            // Batch implement was requested but the batch path cannot run (no batch worker wired,
            // or no eligible children to batch). Surface the downgrade loudly - a visible console
            // line plus a GateFailure-style event - instead of silently falling back to the
            // per-ticket chain. A silent downgrade reads as "batch ran" when it did not, the
            // failure mode op-31 Brief 10 calls out. Mirrors the size-cap fallback logging above.
            var downgradeReason = _batchWorker is null
                ? "no batch worker configured"
                : sharedWorktreePath is null
                    ? "no eligible children to batch"
                    : "batch path unavailable";
            Console.Error.WriteLine(
                $"[{parentTicket.Id}] batch-implement requested but {downgradeReason}; " +
                "running per-ticket chain instead.");
            await _events.EmitAsync(new WorkflowEvent(
                SessionId: _sessionIdGenerator(),
                Timestamp: DateTimeOffset.UtcNow,
                Kind: EventKind.GateFailure,
                TicketId: parentTicket.Id,
                Phase: Phase.Chain,
                Data: new Dictionary<string, object>
                {
                    ["kind"] = "batch_implement_unavailable",
                    ["reason"] = downgradeReason,
                    ["ticket_count"] = eligible.Count
                }), ct).ConfigureAwait(false);
        }

        // Accumulated provides from all previously shipped children. Each child's gate
        // receives this set so the consumes-provides preflight can check whether the child's
        // declared consumes are satisfied by upstream siblings. Updated after every successful ship.
        var accumulatedProvides = new HashSet<string>(StringComparer.Ordinal);

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
                if (chainStartSha is not null)
                {
                    try
                    {
                        var currentTargetSha = await _git.RevParseAsync(integrationBranch, _workingDirectory, ct)
                            .ConfigureAwait(false);
                        childCommitRange = await ChainCommitRangeHelper.ComputeAsync(
                            _git, chainStartSha, currentTargetSha, _workingDirectory, ct).ConfigureAwait(false);
                    }
                    catch { /* non-fatal: pointer stays null */ }
                }

                var childOptions = options with
                {
                    TicketId = child.Id,
                    SharedWorktreePath = null,
                    ChainCommitRange = childCommitRange,
                    Depth = options.Depth + 1,
                    VisitedTicketUuids = AddVisited(options.VisitedTicketUuids, parentTicket.Uuid),
                    ChainTargetBranch = integrationBranch,
                    ChainIntegrationWorktreePath = sharedWorktreePath ?? integrationWorktreePath,
                    AccumulatedUpstreamProvides = accumulatedProvides
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

                // Accumulate the shipped child's provides so subsequent siblings can check their
                // consumes against the growing set in the consumes-provides preflight.
                if (childResult.ShippedProvides is { Count: > 0 } provides)
                    accumulatedProvides.UnionWith(provides);

                if (childResult.Outcome == ChainOutcome.ParentCompleted)
                {
                    // Accumulate the finished sub-chain into this parent's integration branch.
                    // Rebase the sub-chain branch onto the parent's branch before the fast-forward
                    // (same hazard and fix as the root landing, TLB-494): in a fresh run each
                    // sub-chain forks from the parent's current tip so a plain ff works, but a
                    // reused sub-chain branch from a prior run can have diverged. Rebasing first
                    // makes the ff valid again; a conflict stops the chain with the work left safe
                    // on the sub-chain branch.
                    var childIntegrationBranch = ChainIntegrationBranch(child);
                    var childWorktreePath = await ResolveWorktreePathAsync(childIntegrationBranch, child, ct)
                        .ConfigureAwait(false);
                    var accumulateFailure = await RebaseThenFastForwardAsync(
                        child.Id, childIntegrationBranch, childWorktreePath,
                        integrationBranch, sharedWorktreePath ?? integrationWorktreePath,
                        "chain_accumulate", ct).ConfigureAwait(false);
                    if (accumulateFailure is not null)
                    {
                        allChildResults[^1] = childResult with
                        {
                            Outcome = ChainOutcome.ParentStoppedEarly,
                            FinalRationale = accumulateFailure
                        };
                        anyStoppedEarly = true;
                        break;
                    }
                }
            }
        }

        var childResults = allChildResults;

        // Root-chain landing (TLB-492): a nested parent merges its integration branch up into
        // its own parent's integration branch (the ParentCompleted merge above). The OUTERMOST
        // chain (ChainTargetBranch is null) has no parent to merge into, so it lands the
        // accumulated integration branch onto the configured target branch in the main worktree
        // - which the chain preflight pinned to that target and nothing since has moved - and
        // pushes. Without this every leaf ships locally and the whole chain's work strands on a
        // local chain/{root} branch, never reaching the target.
        string? landingRationale = null;
        if (!anyStoppedEarly
            && options.ChainTargetBranch is null
            && sharedWorktreePath is not null)
        {
            landingRationale = await LandRootIntegrationBranchAsync(
                options.TicketId, integrationBranch, sharedWorktreePath, ct).ConfigureAwait(false);
            if (landingRationale is not null)
                anyStoppedEarly = true;
        }

        // Attempt parent rollup (fail-soft)
        try { await _ticketing.RollupParentAsync(options.TicketId, ct).ConfigureAwait(false); }
        catch { /* non-fatal */ }

        totalSw.Stop();
        var outcome = anyStoppedEarly ? ChainOutcome.ParentStoppedEarly : ChainOutcome.ParentCompleted;
        var finalRationale = landingRationale
            ?? (anyStoppedEarly
                ? $"One or more children did not complete: {string.Join(", ", childResults.Where(r => !IsChainSuccess(r.Outcome)).Select(r => r.TicketId))}"
                : $"All {eligible.Count} eligible children completed.");

        return new ChainResult(
            TicketId: options.TicketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: totalSw.Elapsed,
            FinalRationale: finalRationale,
            ChildResults: childResults.AsReadOnly());
    }

    /// <summary>
    /// Plans a single Backlog candidate for a batch group. Planning stays per-ticket (only the
    /// implement/review/ship is batched). Returns null on success, or a failure reason on failure.
    /// Emits plan START/done through the OnStep stream under the candidate's id so the operator sees
    /// the per-ticket planning the same way the per-ticket chain shows it.
    /// </summary>
    private async Task<string?> PlanForBatchAsync(ChainPhaseOptions options, string ticketId, CancellationToken ct)
    {
        var sessionId = _sessionIdGenerator();
        var buildOpts = BuildPhaseOptions(sessionId, ticketId, "plan", targetBranch: options.ChainTargetBranch);
        var childOptions = options with { TicketId = ticketId };
        EmitPhaseStart(childOptions, "plan", -1, sessionId);
        var sw = Stopwatch.StartNew();
        var planResult = await _planFactory(buildOpts).RunAsync(ticketId, _workingDirectory, ct).ConfigureAwait(false);
        sw.Stop();
        childOptions.OnStep?.Invoke(ticketId, new ChainStep(
            PhaseName: "plan",
            ReworkRoundNumber: -1,
            Status: planResult.Success ? Status.Ok : Status.Failed,
            FailureReason: planResult.FailureReason,
            Verdict: null,
            Duration: sw.Elapsed,
            PhaseSessionId: sessionId));
        return planResult.Success ? null : (planResult.FailureReason ?? "planning failed");
    }

    /// <summary>
    /// Ships a reviewed batch stack into the parent integration branch. The warm batch session left
    /// the integration worktree checked out on the batch branch, so this switches it back to the
    /// integration branch and fast-forwards that branch onto the batch stack tip (the leaf-ship
    /// equivalent for the whole stack), then marks each batched ticket Done with a shipped_at marker
    /// so downstream state matches a per-ticket ship. Returns null on success, or a human-readable
    /// reason on failure (the batch work stays safe on the batch branch for a re-run).
    /// </summary>
    private async Task<string?> ShipBatchStackAsync(
        IReadOnlyList<Ticket> batchTickets,
        string batchBranch,
        string integrationBranch,
        string integrationWorktreePath,
        CancellationToken ct)
    {
        var switchResult = await _git.SwitchBranchAsync(integrationBranch, integrationWorktreePath, ct)
            .ConfigureAwait(false);
        if (!switchResult.Success)
        {
            await EmitChainGateFailureAsync(batchTickets[0].Id, "batch_ship_switch_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["batch_branch"] = batchBranch,
                ["detail"] = switchResult.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"batch implemented and reviewed but could not switch the integration worktree onto " +
                $"{integrationBranch} to ship: {switchResult.FailureReason}. The work is safe on {batchBranch}.";
        }

        var ffResult = await _git.FastForwardMergeAsync(batchBranch, integrationWorktreePath, ct)
            .ConfigureAwait(false);
        if (!ffResult.Success)
        {
            await EmitChainGateFailureAsync(batchTickets[0].Id, "batch_ship_merge_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = integrationBranch,
                ["batch_branch"] = batchBranch,
                ["detail"] = ffResult.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"batch implemented and reviewed but fast-forwarding {integrationBranch} onto " +
                $"{batchBranch} failed: {ffResult.FailureReason}. The work is safe on {batchBranch}.";
        }

        string shippedSha;
        try { shippedSha = await _git.HeadShaAsync(integrationWorktreePath, ct).ConfigureAwait(false); }
        catch { shippedSha = "(unknown)"; }

        foreach (var ticket in batchTickets)
        {
            try
            {
                await _ticketing.CreateCommentAsync(ticket.Id,
                    $"<p>[shipped_at: {shippedSha}] (batch into {integrationBranch})</p>", ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: marker posting must not unwind the ship */ }
            try
            {
                await _ticketing.TransitionAsync(ticket.Id, TicketState.Done, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: transition failure must not unwind the ship */ }
            await _events.EmitAsync(new WorkflowEvent(
                SessionId: _sessionIdGenerator(),
                Timestamp: DateTimeOffset.UtcNow,
                Kind: EventKind.StateTransition,
                TicketId: ticket.Id,
                Phase: Phase.Chain,
                Data: new Dictionary<string, object>
                {
                    ["from"] = "InReview",
                    ["to"] = "Done",
                    ["reason"] = "batch_ship"
                }), ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Lands the outermost chain's accumulated integration branch onto the configured target
    /// branch in the main worktree and pushes (when a remote and push are configured). Returns
    /// null on success, or a human-readable rationale describing why the landing stopped. The
    /// accumulated work is never lost on failure - it remains on the integration branch (and,
    /// for a push failure, on the local target) for the operator to reconcile.
    /// </summary>
    private async Task<string?> LandRootIntegrationBranchAsync(
        string ticketId,
        string integrationBranch,
        string integrationWorktreePath,
        CancellationToken ct)
    {
        // The landing fast-forwards whatever HEAD the main worktree points at, so refuse if it
        // is not on the target - mirrors ShipPhase's pre-merge guard and prevents advancing the
        // wrong branch if the preflight invariant was somehow broken mid-run.
        var mainBranch = await _git.CurrentBranchAsync(_workingDirectory, ct).ConfigureAwait(false);
        if (!string.Equals(mainBranch, _baseOptions.TargetBranch, StringComparison.Ordinal))
        {
            await EmitChainGateFailureAsync(ticketId, "chain_landing_wrong_branch", new Dictionary<string, object>
            {
                ["expected"] = _baseOptions.TargetBranch,
                ["actual"] = mainBranch,
                ["integration_branch"] = integrationBranch
            }, ct).ConfigureAwait(false);
            return $"chain accumulated onto {integrationBranch} but the main worktree is on " +
                $"'{mainBranch}', not '{_baseOptions.TargetBranch}'; could not land. The work is " +
                $"safe on {integrationBranch}; switch to {_baseOptions.TargetBranch} and merge it manually.";
        }

        // Rebase the integration branch onto the current target before the fast-forward, then
        // fast-forward the target up to it. The integration branch forks from the target at chain
        // start; if the target then advances - a long chain racing a concurrent push, or a reused
        // integration branch from a prior run after the target moved - the branches diverge and a
        // plain fast-forward is impossible. Replaying onto the current tip makes the target an
        // ancestor again. Shared with the intermediate accumulation merge so the two never diverge.
        var landFailure = await RebaseThenFastForwardAsync(
            ticketId, integrationBranch, integrationWorktreePath,
            _baseOptions.TargetBranch, _workingDirectory, "chain_landing", ct).ConfigureAwait(false);
        if (landFailure is not null)
            return landFailure;

        if (_landingPushEnabled && !string.IsNullOrEmpty(_landingRemote))
        {
            // No-remote guard, symmetric with the per-ticket ShipPhase (which skips fetch/push and
            // emits a no_remote marker when the remote is absent). The local fast-forward above
            // already advanced the target branch, so a missing remote is a clean local land - not a
            // landing failure. Pushing unconditionally would hard-fail a remote-less repo (a fresh
            // `git init`, a smoke-test fixture) even though every child's work is fully integrated.
            var remoteConfigured = await _git.RemoteExistsAsync(_landingRemote!, _workingDirectory, ct)
                .ConfigureAwait(false);
            if (!remoteConfigured)
            {
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: _sessionIdGenerator(),
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.TicketWrite,
                    TicketId: ticketId,
                    Phase: Phase.Chain,
                    Data: new Dictionary<string, object>
                    {
                        ["action"] = "chain_landing_push_skipped",
                        ["reason"] = "no_remote",
                        ["remote"] = _landingRemote!,
                        ["target_branch"] = _baseOptions.TargetBranch
                    }), ct).ConfigureAwait(false);
                Console.WriteLine(
                    $"[{ticketId}] chain landed {integrationBranch} onto {_baseOptions.TargetBranch} " +
                    $"locally; push skipped (no '{_landingRemote}' remote configured).");
                return null;
            }

            var pushResult = await _git.PushAsync(_landingRemote!, _baseOptions.TargetBranch, _workingDirectory, ct)
                .ConfigureAwait(false);
            if (!pushResult.Success)
            {
                await EmitChainGateFailureAsync(ticketId, "chain_landing_push_failed", new Dictionary<string, object>
                {
                    ["target_branch"] = _baseOptions.TargetBranch,
                    ["remote"] = _landingRemote!,
                    ["detail"] = pushResult.FailureReason ?? "unknown"
                }, ct).ConfigureAwait(false);
                return $"landed {integrationBranch} onto {_baseOptions.TargetBranch} locally but push " +
                    $"to {_landingRemote} failed: {pushResult.FailureReason}. Reconcile and push " +
                    $"{_baseOptions.TargetBranch} manually.";
            }
        }

        // Landed cleanly. The chain/{...} integration branches and the per-leaf ticket/{...}
        // branches/worktrees are intentionally retained (not torn down) so a failed or retried
        // chain can resume from the accumulated topology - see SequentialChainTests
        // ParentChain_RetainsIntegrationBranch_AtChainEnd.
        return null;
    }

    /// <summary>
    /// Rebases <paramref name="branch"/> (checked out at <paramref name="branchWorktreePath"/>) onto
    /// <paramref name="targetRef"/>, then fast-forwards <paramref name="targetRef"/> (checked out at
    /// <paramref name="mergeWorktreePath"/>) up to it. Returns null on success, or a human-readable
    /// rationale on failure - the rebase is aborted on conflict and the accumulated work is left
    /// intact on <paramref name="branch"/>. Shared by the intermediate sub-chain accumulation merge
    /// and the root landing so neither breaks when the target advanced after the branch forked.
    /// </summary>
    private async Task<string?> RebaseThenFastForwardAsync(
        string ticketId,
        string branch,
        string branchWorktreePath,
        string targetRef,
        string mergeWorktreePath,
        string failureKindPrefix,
        CancellationToken ct)
    {
        var rebase = await _git.RebaseAsync(targetRef, branchWorktreePath, ct).ConfigureAwait(false);
        if (rebase.HadConflicts)
        {
            await _git.RebaseAbortAsync(branchWorktreePath, ct).ConfigureAwait(false);
            var paths = string.Join(", ", rebase.ConflictingPaths);
            await EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_rebase_conflicts", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["conflicting_paths"] = rebase.ConflictingPaths
            }, ct).ConfigureAwait(false);
            return $"rebasing {branch} onto {targetRef} hit conflicts in: {paths}. The work is safe on " +
                $"{branch}; rebase it onto {targetRef} and resolve, then re-run.";
        }
        if (!rebase.Success)
        {
            await EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_rebase_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["detail"] = rebase.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"rebasing {branch} onto {targetRef} failed: {rebase.FailureReason}. The work is safe " +
                $"on {branch}; rebase it onto {targetRef} manually, then re-run.";
        }

        var ff = await _git.FastForwardMergeAsync(branch, mergeWorktreePath, ct).ConfigureAwait(false);
        if (!ff.Success)
        {
            await EmitChainGateFailureAsync(ticketId, $"{failureKindPrefix}_merge_failed", new Dictionary<string, object>
            {
                ["integration_branch"] = branch,
                ["target_branch"] = targetRef,
                ["detail"] = ff.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return $"landing {branch} onto {targetRef} failed: {ff.FailureReason}. The work is safe on " +
                $"{branch}; merge it manually.";
        }

        return null;
    }

    /// <summary>
    /// Resolves the worktree path that has <paramref name="branch"/> checked out, falling back to
    /// the computed integration worktree path for <paramref name="ticket"/> when git reports no
    /// matching worktree.
    /// </summary>
    private async Task<string> ResolveWorktreePathAsync(string branch, Ticket ticket, CancellationToken ct)
    {
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        var match = worktrees.FirstOrDefault(
            w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
        return match?.Path
            ?? PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, _workingDirectory).WorktreePath;
    }

    private async Task EmitChainGateFailureAsync(string ticketId, string kind, Dictionary<string, object> data, CancellationToken ct)
    {
        data["kind"] = kind;
        await _events.EmitAsync(new WorkflowEvent(
            SessionId: _sessionIdGenerator(),
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.GateFailure,
            TicketId: ticketId,
            Phase: Phase.Chain,
            Data: data), ct).ConfigureAwait(false);
    }

    private static IReadOnlySet<string> AddVisited(IReadOnlySet<string>? visited, string uuid)
    {
        var next = visited is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(visited, StringComparer.Ordinal);
        next.Add(uuid);
        return next;
    }

    private static string ChainIntegrationBranch(Ticket ticket) => ChainIntegrationBranchFromId(ticket.Id);

    private static string ChainIntegrationBranchFromId(string ticketId) => $"chain/{SlugBuilder.BuildTicketSlug(ticketId)}";

    private async Task<WorktreeCreateResult> EnsureIntegrationWorktreeAsync(
        string branch,
        string fromRef,
        string worktreePath,
        CancellationToken ct)
    {
        var existingWorktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        var existingWorktree = existingWorktrees.FirstOrDefault(
            w => string.Equals(w.Branch, branch, StringComparison.OrdinalIgnoreCase));
        if (existingWorktree is not null)
            return new WorktreeCreateResult(true, null, existingWorktree.Path);

        var existingBranches = await _git.ListLocalBranchesAsync(branch, _workingDirectory, ct).ConfigureAwait(false);
        if (existingBranches.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase)))
            return await _git.CheckoutWorktreeAsync(worktreePath, branch, _workingDirectory, ct).ConfigureAwait(false);

        var createResult = await _git.CreateWorktreeAsync(
            worktreePath,
            branch,
            fromRef,
            _workingDirectory,
            ct).ConfigureAwait(false);
        if (createResult.Success)
            return createResult;

        existingBranches = await _git.ListLocalBranchesAsync(branch, _workingDirectory, ct).ConfigureAwait(false);
        if (existingBranches.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase)))
            return await _git.CheckoutWorktreeAsync(worktreePath, branch, _workingDirectory, ct).ConfigureAwait(false);

        return createResult;
    }

    private sealed record DryRunItem(
        Ticket Ticket,
        int Depth,
        bool HasLiveChildren,
        string IntegrationBranch,
        string BaseBranch);

    private sealed record DryRunPlan(
        Ticket Root,
        IReadOnlyList<DryRunItem> PostOrder,
        IReadOnlyList<string> Warnings);

    private async Task<DryRunPlan> BuildDryRunPlanAsync(Ticket root, int maxDepth, CancellationToken ct)
    {
        var items = new List<DryRunItem>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        await VisitDryRunAsync(root, depth: 0, parentIntegrationBranch: _baseOptions.TargetBranch, maxDepth, visited, items, warnings, ct)
            .ConfigureAwait(false);

        return new DryRunPlan(root, items.AsReadOnly(), warnings.AsReadOnly());
    }

    private async Task VisitDryRunAsync(
        Ticket ticket,
        int depth,
        string parentIntegrationBranch,
        int maxDepth,
        HashSet<string> visited,
        List<DryRunItem> items,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!visited.Add(ticket.Uuid))
        {
            warnings.Add($"cycle detected at {ticket.Id}; subtree omitted");
            return;
        }

        var children = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        var eligible = children
            .Where(c => c.State != TicketState.Done && c.State != TicketState.Cancelled)
            .Where(c => !string.Equals(c.Uuid, ticket.Uuid, StringComparison.Ordinal))
            .OrderBy(c => TicketNumber(c.Id))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var integrationBranch = eligible.Count > 0
            ? ChainIntegrationBranch(ticket)
            : PhaseWorktreeLayout.BranchName(ticket.Id);

        if (eligible.Count > 0)
        {
            if (depth >= maxDepth)
            {
                warnings.Add($"depth cap {maxDepth} reached at {ticket.Id}; subtree omitted");
            }
            else
            {
                var graph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
                var levels = TopologicalSorter.ComputeLevels(graph);
                foreach (var childId in levels.SelectMany(level => level))
                {
                    var child = eligible.First(c => string.Equals(c.Id, childId, StringComparison.Ordinal));
                    await VisitDryRunAsync(child, depth + 1, integrationBranch, maxDepth, visited, items, warnings, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        items.Add(new DryRunItem(ticket, depth, eligible.Count > 0, integrationBranch, parentIntegrationBranch));
        visited.Remove(ticket.Uuid);
    }

    private static void PrintDryRunPlan(DryRunPlan plan, int maxDepth)
    {
        Console.WriteLine($"[{plan.Root.Id}] dry-run chain plan (max depth {maxDepth}):");
        Console.WriteLine("post-order schedule:");
        for (int i = 0; i < plan.PostOrder.Count; i++)
        {
            var item = plan.PostOrder[i];
            var action = item.HasLiveChildren
                ? $"roll up internal node on {item.IntegrationBranch}"
                : "run plan/implement/review/ship";
            Console.WriteLine($"  {i + 1}. {new string(' ', item.Depth * 2)}{item.Ticket.Id} - {action}");
        }

        Console.WriteLine("branch topology:");
        foreach (var item in plan.PostOrder.Where(i => i.HasLiveChildren).OrderBy(i => i.Depth).ThenBy(i => TicketNumber(i.Ticket.Id)))
            Console.WriteLine($"  {item.IntegrationBranch} from {item.BaseBranch} integrates subtree for {item.Ticket.Id}");
        foreach (var item in plan.PostOrder.Where(i => !i.HasLiveChildren))
            Console.WriteLine($"  {PhaseWorktreeLayout.BranchName(item.Ticket.Id)} from {item.BaseBranch} before {item.Ticket.Id}");

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine("warnings:");
            foreach (var warning in plan.Warnings)
                Console.WriteLine($"  {warning}");
        }
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
        // No project-identifier prefix (identifier not configured): the whole id is a
        // bare number (e.g. "10"). Parse it directly so unordered siblings still sort
        // numerically instead of falling through to a lexicographic id tiebreaker
        // (where "10" < "8"). See TLB-496 for the negative-id sibling of this bug.
        if (dash < 0 && int.TryParse(id, out var bare))
            return bare;
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
