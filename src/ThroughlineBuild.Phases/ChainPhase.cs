using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Verification;
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
    // Bound on the deterministic check-recheck loop: when a rework round was triggered by named
    // failing checks and the worker's fix still fails the re-run, the raw output loops straight
    // back to the worker up to this many times per rework round - without consuming a rework
    // round or a verifier call. A check is an oracle; a subprocess re-run proves in seconds what
    // a verifier LLM call rediscovers in minutes.
    private const int MaxCheckRetriesPerReworkRound = 2;
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
    private readonly ChainIntegrationBranch _integrationBranch;
    private readonly BatchImplementRunner _batchImplementRunner;
    private readonly BatchReviewRunner _batchReviewRunner;
    // Optional: recovers the latest Rework verdict from the event log so an in-progress ticket
    // that carries real work can be resumed with its prior feedback. Null falls back to a
    // synthesized resume note (e.g. an interrupted initial implement that was never reviewed).
    private readonly IReviewFeedbackRetriever? _feedbackRetriever;
    // Optional: when set, batch implement groups in the parent chain dispatch one session here
    // instead of running a per-ticket implement+review+ship loop for each group member.
    private readonly IWorkerAgent? _batchWorker;
    // Optional: the configured check specs + runner for the post-rework deterministic re-run.
    // When either is null the recheck is skipped and rework rounds flow exactly as before.
    private readonly IReadOnlyList<CheckSpec>? _reworkRecheckSpecs;
    private readonly AutomatedChecksRunner? _reworkRecheckRunner;
    private readonly TextWriter? _output;

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
        Func<BuildOptions, GatePhase>? gateFactory = null,
        IReadOnlyList<CheckSpec>? reworkRecheckSpecs = null,
        AutomatedChecksRunner? reworkRecheckRunner = null,
        TextWriter? output = null)
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
        _integrationBranch = new ChainIntegrationBranch(
            _git, _workingDirectory, landingRemote, landingPushEnabled);
        _feedbackRetriever = feedbackRetriever;
        _batchWorker = batchWorker;
        _batchImplementRunner = new BatchImplementRunner(
            batchWorker,
            _ticketing,
            _git,
            _baseOptions,
            _workingDirectory,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            _planFactory,
            (sessionId, ticketId, phaseName, round, targetBranch) =>
                BuildPhaseOptions(sessionId, ticketId, phaseName, round, targetBranch));
        _batchReviewRunner = new BatchReviewRunner(
            batchWorker,
            _ticketing,
            _git,
            _baseOptions,
            _workingDirectory,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            _implementFactory,
            (sessionId, ticketId, phaseName, round, targetBranch) =>
                BuildPhaseOptions(sessionId, ticketId, phaseName, round, targetBranch));
        _reworkRecheckSpecs = reworkRecheckSpecs;
        _reworkRecheckRunner = reworkRecheckRunner;
        _output = output;
    }

    // Inert read-only accessors over the wired collaborators, for composition-root tests that
    // verify the chain verb did not silently drop a dependency when constructing this phase.
    // The original --batch-implement bug was exactly a dropped ctor argument (_batchWorker left
    // null), so these let a test fail when that recurs. No runtime behavior depends on them.
    internal IWorkerAgent? BatchWorker => _batchImplementRunner.BatchWorker;
    internal Func<BuildOptions, ShipPhase>? ChainShipFactory => _chainShipFactory;

    private ChainEventEmitter EventEmitter(string sessionId) =>
        new(_events, _ticketing, sessionId);

    private TextWriter Output => _output ?? Console.Out;

    public async Task<ChainResult> RunAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        // TLB-545: a ticketing backend that is unreachable at the transport level (after the
        // client's own retries) is an environmental failure, not the ticket's fault - and the
        // ticket's work is always committed to its branch before any ticketing write, so the
        // chain is resumable. Classify it here, at the same per-ticket boundary the recursion
        // uses for children, so one dead backend stops the run cleanly instead of crashing the
        // process: the parent loop and dispatcher see a result (not an exception) and mark the
        // remaining siblings/roots Skipped via ContainsEnvironmentalStop.
        var classifySw = Stopwatch.StartNew();
        try
        {
            return await RunChainCoreAsync(options, ct).ConfigureAwait(false);
        }
        catch (TicketingUnavailableException ex)
        {
            classifySw.Stop();
            Console.Error.WriteLine(
                $"[{options.TicketId}] chain stopped: ticketing backend unreachable - {ex.Message}");
            var result = new ChainResult(options.TicketId, Array.Empty<ChainStep>(),
                ChainOutcome.TicketingUnavailable, classifySw.Elapsed, ex.Message);
            // Best-effort forensics: the event log is local (file sink), but never let a
            // logging failure mask the classified result.
            try
            {
                await EventEmitter(_sessionIdGenerator())
                    .EmitChainEndAsync(result, options.TicketId, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
            return result;
        }
    }

    private async Task<ChainResult> RunChainCoreAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var steps = new List<ChainStep>();

        var chainSessionId = _sessionIdGenerator();

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        if (await RunOutermostPreflightAsync(options, ticket, steps, totalSw, chainSessionId, ct).ConfigureAwait(false) is { } preflightRefusal) return preflightRefusal;

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
            var planner = new ChainDryRunPlanner(_ticketing, Output);
            var plan = await planner
                .BuildAsync(ticket, _baseOptions.TargetBranch, options.MaxDepth, ct)
                .ConfigureAwait(false);
            planner.Print(plan, options.MaxDepth);
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
        // states are genuinely un-runnable. ChainResumeResolver performs any reset/prune side effects.
        var entry = await new ChainResumeResolver(
                _ticketing,
                _git,
                _feedbackRetriever,
                EventEmitter(chainSessionId))
            .ResolveAsync(ticket, _workingDirectory, _baseOptions.TargetBranch, ct)
            .ConfigureAwait(false);
        var startPhase = entry.StartPhase;

        var startingAtPhaseStr = startPhase switch
        {
            StartPhase.Plan => "plan",
            StartPhase.Implement => "implement",
            StartPhase.ResumeImplement => "implement",
            StartPhase.Review => "review",
            _ => "refused"
        };

        await EventEmitter(chainSessionId).EmitAsync(
            EventKind.ChainStart,
            options.TicketId,
            Phase.Chain,
            new Dictionary<string, object>
            {
                ["starting_at_phase"] = startingAtPhaseStr,
                ["initial_state"] = ticket.State.ToString(),
                ["chain_session_id"] = chainSessionId
            }, ct).ConfigureAwait(false);

        if (startPhase == StartPhase.Refused)
        {
            totalSw.Stop();
            var refusedResult = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedInitialState,
                totalSw.Elapsed, null);
            await EventEmitter(chainSessionId)
                .EmitChainEndAsync(refusedResult, options.TicketId, ct).ConfigureAwait(false);
            return refusedResult;
        }

        if (startPhase == StartPhase.Plan)
        {
            var sessionId = _sessionIdGenerator();
            var buildOpts = BuildPhaseOptions(sessionId, options.TicketId, "plan", targetBranch: options.ChainTargetBranch);
            EventEmitter(chainSessionId).EmitPhaseStart(options, "plan", -1, sessionId);
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
                        await EventEmitter(chainSessionId).BestEffortTicketWriteAsync(
                            options.TicketId,
                            "subsumed_rationale_comment",
                            ticketing => ticketing.CreateCommentAsync(
                                options.TicketId,
                                "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>",
                                ct),
                            ct).ConfigureAwait(false);
                        await EventEmitter(chainSessionId).EmitAsync(
                            EventKind.TicketSubsumed,
                            options.TicketId,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }, ct).ConfigureAwait(false);
                        totalSw.Stop();
                        var ratified = new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence);
                        await EventEmitter(chainSessionId)
                            .EmitChainEndAsync(ratified, options.TicketId, ct).ConfigureAwait(false);
                        return ratified;
                    }
                    // Ratifier rejected - fall through to StoppedAtPlan
                }
                totalSw.Stop();
                var stoppedAtPlan = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtPlan,
                    totalSw.Elapsed, null);
                await EventEmitter(chainSessionId)
                    .EmitChainEndAsync(stoppedAtPlan, options.TicketId, ct).ConfigureAwait(false);
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
                await EventEmitter(chainSessionId)
                    .EmitChainEndAsync(finalResult, options.TicketId, ct).ConfigureAwait(false);
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
                await EventEmitter(chainSessionId)
                    .EmitChainEndAsync(finalResult, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
            shippedProvides = loopProvides;
        }

        var shipSessionId = _sessionIdGenerator();
        var shipBuildOpts = BuildPhaseOptions(shipSessionId, options.TicketId, "ship", targetBranch: options.ChainTargetBranch);
        EventEmitter(chainSessionId).EmitPhaseStart(options, "ship", -1, shipSessionId);
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
            await EventEmitter(chainSessionId)
                .EmitChainEndAsync(stoppedAtShip, options.TicketId, ct).ConfigureAwait(false);
            return stoppedAtShip;
        }

        var completed = new ChainResult(options.TicketId, steps, ChainOutcome.Completed, totalSw.Elapsed, null,
            ShippedProvides: shippedProvides);
        if (options.ChainTargetBranch is null)
            await _integrationBranch.SweepChainWorktreesAsync(
                options.TicketId, EventEmitter(chainSessionId), ct).ConfigureAwait(false);
        await EventEmitter(chainSessionId)
            .EmitChainEndAsync(completed, options.TicketId, ct).ConfigureAwait(false);
        return completed;
    }

    private async Task<ChainResult?> RunOutermostPreflightAsync(
        ChainPhaseOptions options,
        Ticket ticket,
        IReadOnlyList<ChainStep> steps,
        Stopwatch totalSw,
        string chainSessionId,
        CancellationToken ct)
    {
        // Children recurse with ChainTargetBranch set, so preflight runs only at the
        // outermost entry, before any planning or mutation.
        if (options.ChainTargetBranch is not null)
            return null;

        var preflight = new ChainPreflight(_git, _workingDirectory, _baseOptions.TargetBranch);
        var refusal = await preflight
            .CheckAsync(PhaseWorktreeLayout.BranchName(ticket.Id), ct)
            .ConfigureAwait(false);
        if (refusal is null)
            return null;

        // Preserve the existing diagnostics: hygiene failures emit an event but do not
        // write this refusal line.
        if (refusal.Outcome == ChainOutcome.RefusedWrongBranch
            || refusal.DirtyTreeCause == DirtyTreeCause.TrackedChanges)
        {
            Console.Error.WriteLine($"[{options.TicketId}] chain refused: {refusal.Message}");
        }

        await EventEmitter(chainSessionId).EmitAsync(
            EventKind.GateFailure,
            options.TicketId,
            Phase.Chain,
            refusal.EventData,
            ct).ConfigureAwait(false);

        totalSw.Stop();
        var result = new ChainResult(
            options.TicketId,
            steps,
            refusal.Outcome,
            totalSw.Elapsed,
            refusal.Message,
            DirtyTreeCause: refusal.DirtyTreeCause);
        await EventEmitter(chainSessionId)
            .EmitChainEndAsync(result, options.TicketId, ct).ConfigureAwait(false);
        return result;
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
        var eventEmitter = EventEmitter(chainSessionId);

        // Carries the prior round's HEAD across iterations so a rework round can record the
        // commit sha BEFORE it ran (sha_after comes from the round's own implResult). Null on
        // the first round (or a resume), where the prior sha is not known in this invocation.
        string? priorCommitSha = null;

        // Gate cost ledger accumulators: track gate wall time and gate-attributable rework tokens
        // across all iterations so a single CostLedger event is emitted per ticket at exit.
        long gateWallMs = 0;
        int gateAttributableReworkRounds = 0;
        long gateAttributableReworkInputTokens = 0;
        long gateAttributableReworkOutputTokens = 0;
        bool gateAttributableReworkTokensTracked = false;
        bool thisRoundIsGateAttributable = false;
        bool gateWasEngaged = false;

        // Check-recheck retry state: retries within one rework round (does not consume `round`),
        // plus whether the round being retried was gate-originated so the cost ledger keeps
        // attributing the retry implements to the gate.
        int checkRetriesThisRound = 0;
        bool recheckRetryGateAttributable = false;

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = BuildPhaseOptions(implSessionId, options.TicketId, "implement", round, options.ChainTargetBranch);
            // Pass the chain's prior-commit pointer on the first implement round only.
            // Rework rounds (feedback != null) reuse the same worktree with the agent's
            // own edits already in place, so replaying the handoff is redundant.
            var implChainRange = (feedback is null) ? options.ChainCommitRange : null;
            var implPhaseOpts = new ImplementPhaseOptions(feedback, options.SharedWorktreePath, implChainRange);
            eventEmitter.EmitPhaseStart(options, "implement", round, implSessionId);
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

            // --debug side channel: when this implement round was driven by prior feedback it
            // IS a rework. Record what triggered it (gate vs reviewer), the failure payload
            // verbatim, and the commit shas before/after - the inputs analysis needs to split
            // design misses from hygiene slips. No-op when debug capture is off.
            if (feedback is not null)
                ReworkRoundManifest.Write(implBuildOpts.DebugCaptureDirectory, round, feedback,
                    shaBefore: priorCommitSha, shaAfter: implResult.CommitSha);
            priorCommitSha = implResult.CommitSha;

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
                        await eventEmitter.BestEffortTicketWriteAsync(
                            options.TicketId,
                            "subsumed_rationale_comment",
                            ticketing => ticketing.CreateCommentAsync(
                                options.TicketId,
                                "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>",
                                ct),
                            ct).ConfigureAwait(false);
                        await eventEmitter.EmitAsync(
                            EventKind.TicketSubsumed,
                            options.TicketId,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }, ct).ConfigureAwait(false);
                        totalSw.Stop();
                        return (new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence), null);
                    }
                    // Ratifier rejected - fall through to StoppedAtImplement
                }
                if (gateWasEngaged)
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtImplement,
                    TimeSpan.Zero, null), null);
            }

            // Deterministic post-rework check re-run: when this implement round was rework
            // triggered by named failing checks, re-run exactly those checks (pure subprocess,
            // no LLM) before spending a gate run or a verifier call. The check is the oracle for
            // a check-driven rework - a worker that "fixed" the violation without ever re-running
            // the check is caught here in seconds instead of by the next verifier round. Still-
            // failing gating/setup checks loop the raw output straight back to the worker
            // (bounded by MaxCheckRetriesPerReworkRound, not consuming a rework round); advisory
            // results never trigger the short-circuit (role semantics are a cross-phase contract).
            if (feedback is not null && feedback.ChecksFailed.Count > 0
                && _reworkRecheckSpecs is { Count: > 0 } && _reworkRecheckRunner is not null)
            {
                var recheckWorktree = implResult.WorktreePath ?? _workingDirectory;
                var recheckResults = new List<CheckResult>();
                foreach (var name in feedback.ChecksFailed.Distinct(StringComparer.Ordinal))
                {
                    recheckResults.Add(await _reworkRecheckRunner
                        .RunNamedAsync(name, _reworkRecheckSpecs, recheckWorktree, ct).ConfigureAwait(false));
                }
                var stillFailing = recheckResults
                    .Where(r => !r.Skipped && !r.Passed && r.Role != CheckRole.Advisory)
                    .ToList();

                if (stillFailing.Count > 0)
                {
                    var stillFailingNames = stillFailing.Select(r => r.Name).ToList();
                    await eventEmitter.EmitAsync(
                        EventKind.GateFailure,
                        options.TicketId,
                        Phase.Implement,
                        new Dictionary<string, object>
                        {
                            ["kind"] = "rework_recheck_failed",
                            ["round"] = round,
                            ["retry"] = checkRetriesThisRound + 1,
                            ["checks_still_failing"] = stillFailingNames
                        }, ct).ConfigureAwait(false);

                    var recheckRationale =
                        $"Post-rework re-run: the failing check(s) that triggered rework round {round} " +
                        $"STILL FAIL after the changes: {string.Join(", ", stillFailingNames)}. " +
                        "The previous fix attempt did not satisfy the check; its verbatim output follows.";

                    if (checkRetriesThisRound < MaxCheckRetriesPerReworkRound)
                    {
                        // First retry inherits the round's gate attribution; later retries keep it.
                        if (checkRetriesThisRound == 0)
                            recheckRetryGateAttributable = feedback.GateFailedChecks is { Count: > 0 };
                        thisRoundIsGateAttributable = recheckRetryGateAttributable;
                        checkRetriesThisRound++;

                        // Implement left the ticket InReview; a rework implement requires
                        // InProgress (the gate does this same bounce on a hard-fail).
                        await _ticketing.TransitionAsync(options.TicketId, TicketState.InProgress, ct).ConfigureAwait(false);

                        feedback = new ReviewFeedback(recheckRationale, stillFailingNames, round,
                            FailedCheckDetails: stillFailing);
                        continue;
                    }

                    // Retries exhausted: surface the raw check output in the abort rationale (the
                    // triage) instead of burning rework rounds or a verifier call on a fix the
                    // checks already disprove.
                    var failTail = string.Join("\n", stillFailing.Select(r =>
                        $"- {r.Name} (exit {r.ExitCode}; command: {r.CommandLine}): " +
                        (string.IsNullOrWhiteSpace(r.StderrTail) ? r.StdoutTail.Trim() : r.StderrTail.Trim())));
                    if (gateWasEngaged)
                        await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                    return (new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                        TimeSpan.Zero, recheckRationale + "\n" + failTail), null);
                }

                checkRetriesThisRound = 0;
                recheckRetryGateAttributable = false;
            }

            // Gate phase: run checks once on the warm worktree and collect smoke signals.
            // Only engaged when a gate factory was supplied; nil factory skips gate entirely.
            GateOutcome? gateOutcome = null;
            if (_gateFactory is not null)
            {
                var gateSessionId = _sessionIdGenerator();
                var gateBuildOpts = BuildPhaseOptions(gateSessionId, options.TicketId, "gate", round, options.ChainTargetBranch);
                eventEmitter.EmitPhaseStart(options, "gate", round, gateSessionId);
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
                    if (gateOutcome.Vacuous)
                    {
                        // Gate integrity failure (vacuous gating check or canary cleanup failure): a config/setup
                        // defect, not a code defect. Reworking the implementer cannot fix it, so hard-fail the chain
                        // here WITHOUT a rework round. As a chain FAILURE, preserve-on-failure leaves the worktrees
                        // in place for inspection.
                        if (gateWasEngaged)
                            await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                        return (new ChainResult(options.TicketId, steps, ChainOutcome.GateVacuous, TimeSpan.Zero, gateOutcome.HardFailReason), null);
                    }

                    if (gateOutcome.EnvironmentFailure)
                    {
                        // Environment failure (TLB-538): the control run proved the same gating checks
                        // fail on the untouched base ref, so reworking the implementer cannot fix it -
                        // hard-fail WITHOUT a rework round. The gate left the ticket InReview (no
                        // InProgress bounce), so a re-run after the environment fix resumes cleanly.
                        // false_fails records how many gate hard-fails were proven environmental.
                        var falseFails = gateOutcome.CheckResults
                            .Count(r => r.Role == CheckRole.Gating && !r.Passed && !r.Skipped);
                        if (gateWasEngaged)
                            await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct, falseFails: Math.Max(falseFails, 1)).ConfigureAwait(false);
                        return (new ChainResult(options.TicketId, steps, ChainOutcome.GateEnvironmentFailure, TimeSpan.Zero, gateOutcome.HardFailReason), null);
                    }

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
                        await eventEmitter.EmitAsync(
                            EventKind.ReworkRound,
                            options.TicketId,
                            Phase.Implement,
                            new Dictionary<string, object>
                            {
                                ["round"] = round + 1,
                                ["verdict_that_triggered"] = "GateFailure",
                                ["rationale_preview"] = ChainEventEmitter.RationalePreview(gateRationale) ?? ""
                            }, ct).ConfigureAwait(false);
                        thisRoundIsGateAttributable = true;
                        round++;
                        continue;
                    }
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                    return (new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                        TimeSpan.Zero, gateRationale), null);
                }
            }

            var reviewResult = await RunOneReviewAsync(options, steps, round, gateOutcome, ct).ConfigureAwait(false);

            if (reviewResult.abort is not null)
            {
                if (gateWasEngaged)
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (reviewResult.abort, null);
            }

            var rv = reviewResult.verdict!;

            if (rv.Kind == VerdictKind.Pass)
            {
                if (gateWasEngaged)
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (null, implResult.CompletionClaim?.Provides);
            }

            if (rv.Kind == VerdictKind.Fail)
            {
                if (gateWasEngaged)
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
                return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                    TimeSpan.Zero, rv.Rationale), null);
            }

            if (round < MaxReworkRounds)
            {
                feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1,
                    FailedCheckDetails: MatchFailedCheckDetails(rv.ChecksFailed, reviewResult.checkResults));
                await eventEmitter.EmitAsync(
                    EventKind.ReworkRound,
                    options.TicketId,
                    Phase.Implement,
                    new Dictionary<string, object>
                    {
                        ["round"] = round + 1,
                        ["verdict_that_triggered"] = "Rework",
                        ["rationale_preview"] = ChainEventEmitter.RationalePreview(rv.Rationale) ?? ""
                    }, ct).ConfigureAwait(false);
                round++;
            }
            else
            {
                if (gateWasEngaged)
                    await eventEmitter.EmitCostLedgerAsync(options.TicketId, gateWallMs, gateAttributableReworkRounds, gateAttributableReworkInputTokens, gateAttributableReworkOutputTokens, gateAttributableReworkTokensTracked, ct).ConfigureAwait(false);
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

        var feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1,
            FailedCheckDetails: MatchFailedCheckDetails(rv.ChecksFailed, reviewResult.checkResults));
        await EventEmitter(chainSessionId).EmitAsync(
            EventKind.ReworkRound,
            options.TicketId,
            Phase.Implement,
            new Dictionary<string, object>
            {
                ["round"] = round + 1,
                ["verdict_that_triggered"] = "Rework",
                ["rationale_preview"] = ChainEventEmitter.RationalePreview(rv.Rationale) ?? ""
            }, ct).ConfigureAwait(false);
        return await RunImplementReviewLoopAsync(options, steps, chainSessionId, round + 1, feedback, totalSw, ct)
            .ConfigureAwait(false);
    }

    private async Task<(ChainResult? abort, Verdict? verdict, IReadOnlyList<CheckResult>? checkResults)> RunOneReviewAsync(
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
        EventEmitter(revSessionId).EmitPhaseStart(options, "review", -1, revSessionId);
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

            // A provider block (quota/rate-limit/auth) is not a review failure - the verifier never
            // ran. Surface a distinct, resumable ReviewUnavailable outcome instead of StoppedAtReview,
            // so the operator sees "re-run once quota resets", not "review rejected the diff". This
            // single branch covers both the implement->review loop and the InReview resume path, since
            // both funnel a provider error through ReviewPhase returning Success=false. See TLB-527.
            var failOutcome = revResult.ProviderUnavailable is not null
                ? ChainOutcome.ReviewUnavailable
                : ChainOutcome.StoppedAtReview;
            return (new ChainResult(options.TicketId, steps, failOutcome,
                TimeSpan.Zero, revResult.FailureReason), null, null);
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

        return (null, new Verdict(revResult.Verdict!.Value, revResult.VerdictRationale ?? "", revResult.ChecksFailed),
            revResult.CheckResults);
    }

    // Raw results for the checks the verifier cited, so review-originated rework feedback carries
    // the checks' own output (command, exit code, stdout/stderr) - not just names plus the
    // reviewer's theory of why the tool failed, which can be confidently wrong.
    private static IReadOnlyList<CheckResult>? MatchFailedCheckDetails(
        IReadOnlyList<string> checksFailed,
        IReadOnlyList<CheckResult>? checkResults)
    {
        if (checksFailed.Count == 0 || checkResults is null)
            return null;
        var named = new HashSet<string>(checksFailed, StringComparer.Ordinal);
        var details = checkResults.Where(r => named.Contains(r.Name) && !r.Passed && !r.Skipped).ToList();
        return details.Count > 0 ? details : null;
    }

    private static string FormatSubsumedRationale(SubsumedByEvidence? evidence)
    {
        var commit = evidence?.Commit ?? "(unknown)";
        var rationale = evidence?.Rationale ?? "(no rationale)";
        var files = evidence?.Files is { Count: > 0 } f ? string.Join(", ", f) : "(none)";
        return $"Subsumed by {commit}: {rationale}; files: {files}";
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

        EventEmitter(sessionId).EmitPhaseStart(options, "ratify", -1, sessionId);
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
            .OrderBy(c => TicketIdOrdering.Number(c.Id))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // Build a level-based execution schedule from sibling blocked_by relations so that
        // dependent siblings are serialized while independent siblings still run in order.
        var siblingGraph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
        var levels = TopologicalSorter.ComputeLevels(siblingGraph);

        // Print the dependency order derived from Plane before any phase runs so a wrong
        // or missing edge is visible up front. Tickets in the same level have no blocked_by
        // edge between them and are unordered relative to each other (Brief 17).
        new ChainDryRunPlanner(_ticketing, Output).PrintDispatchOrder(options.TicketId, levels);

        var integrationBaseRef = options.ChainTargetBranch ?? _baseOptions.TargetBranch;
        var integrationNames = PhaseWorktreeLayout.Compute(parentTicket.Id, parentTicket.Title, _workingDirectory);
        var integrationBranch = ChainIntegrationBranch.BranchName(parentTicket);
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
            var createResult = await _integrationBranch.EnsureIntegrationWorktreeAsync(
                integrationBranch,
                integrationBaseRef,
                integrationWorktreePath,
                ct).ConfigureAwait(false);
            if (createResult.Success)
            {
                sharedWorktreePath = createResult.AbsolutePath ?? integrationWorktreePath;

                // TLB-546: a retained chain/{slug} branch from a prior run stays frozen at the
                // base tip it forked from. Reconcile it with the CURRENT base before any child
                // dispatches - otherwise every child implements against a stale snapshot and the
                // divergence only surfaces at the root landing, after all the work is burned.
                var refreshFailure = await _integrationBranch.RefreshIntegrationBranchAsync(
                    parentTicket.Id, integrationBranch, sharedWorktreePath,
                    integrationBaseRef, () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
                if (refreshFailure is not null)
                {
                    totalSw.Stop();
                    return new ChainResult(
                        options.TicketId,
                        Array.Empty<ChainStep>(),
                        ChainOutcome.ParentStoppedEarly,
                        totalSw.Elapsed,
                        refreshFailure,
                        ChildResults: Array.Empty<ChainResult>());
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"[{parentTicket.Id}] integration worktree unavailable " +
                    $"({createResult.FailureReason}); cannot safely accumulate nested chain branches.");
                await EventEmitter(_sessionIdGenerator()).EmitAsync(
                    EventKind.GateFailure,
                    parentTicket.Id,
                    Phase.Chain,
                    new Dictionary<string, object>
                    {
                        ["kind"] = "integration_worktree_unavailable",
                        ["detail"] = createResult.FailureReason ?? "unknown",
                        ["branch"] = integrationBranch,
                        ["path"] = integrationWorktreePath
                    }, ct).ConfigureAwait(false);
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
        // TLB-538/TLB-545: set when a stopped child's failure was environmental - an environment
        // gate failure or an unreachable ticketing backend. Both are global to the machine/config,
        // so the remaining siblings are marked Skipped instead of silently omitted - the operator
        // sees the full blast radius, and nothing else is dispatched into the same wall.
        bool environmentFailureDetected = false;
        string? environmentSkipReason = null;

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
                        await EventEmitter(_sessionIdGenerator()).EmitAsync(
                            EventKind.GateFailure,
                            parentTicket.Id,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["kind"] = "batch_skip_internal_node",
                                ["ticket"] = candidate.Id
                            }, ct).ConfigureAwait(false);
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
                        var planReason = await _batchImplementRunner
                            .PlanForBatchAsync(options, candidate.Id, ct).ConfigureAwait(false);
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
                    var capViolation = BatchImplementRunner.CheckBatchSizeCaps(
                        batchTickets, _baseOptions);
                    if (capViolation is not null)
                    {
                        Console.Error.WriteLine(
                            $"[{parentTicket.Id}] batch-size-fallback: cap exceeded ({capViolation}); " +
                            $"running per-ticket chain for all {batchTickets.Count} ticket(s) instead.");
                        await EventEmitter(_sessionIdGenerator()).EmitAsync(
                            EventKind.GateFailure,
                            parentTicket.Id,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["kind"] = "batch_size_cap_exceeded",
                                ["cap"] = capViolation,
                                ["ticket_count"] = batchTickets.Count
                            }, ct).ConfigureAwait(false);
                        // batchedTicketIds stays null: the now-Ready planned tickets fall through to
                        // the per-ticket level loop and resume at implement (no re-plan).
                    }
                    else
                    {
                        batchedTicketIds = new HashSet<string>(
                            batchTickets.Select(t => t.Id), StringComparer.Ordinal);

                        var batchOutcome = await _batchImplementRunner.RunBatchImplementSessionAsync(
                            options, batchTickets, sharedWorktreePath, integrationBranch, chainStartSha, ct)
                            .ConfigureAwait(false);

                        allChildResults.AddRange(batchOutcome.Results);
                        var batchFailure = batchOutcome.Results.FirstOrDefault(
                            br => !IsChainSuccess(br.Outcome) && br.Outcome != ChainOutcome.Skipped);
                        if (batchFailure is not null)
                        {
                            anyStoppedEarly = true;
                            environmentFailureDetected = batchFailure.ContainsEnvironmentalStop();
                            if (environmentFailureDetected)
                                environmentSkipReason = batchFailure.ContainsTicketingUnavailable()
                                    ? "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain"
                                    : "environment gate failure in a sibling; fix the environment once and re-run the chain";
                        }

                        if (!anyStoppedEarly
                            && _batchWorker is not null
                            && batchOutcome.ConfirmedTickets is not null
                            && batchOutcome.ConfirmedTickets.Count > 0)
                        {
                            try
                            {
                                var batchReviewPassed = await _batchReviewRunner.RunBatchReviewAndReworkAsync(
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
                                    var shipReason = await _integrationBranch.ShipBatchStackAsync(
                                        batchTickets, batchOutcome.BranchName, integrationBranch,
                                        sharedWorktreePath, _ticketing,
                                        () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
                                    if (shipReason is not null)
                                    {
                                        Console.Error.WriteLine($"[{parentTicket.Id}] batch ship failed: {shipReason}");
                                        anyStoppedEarly = true;
                                    }
                                }
                            }
                            catch (BatchTicketingUnavailableException ex)
                            {
                                ChainEventEmitter.RecordBatchTicketingUnavailable(
                                    allChildResults, batchTickets, ex.TicketId, ex.TicketingException);
                                anyStoppedEarly = true;
                                environmentFailureDetected = true;
                                environmentSkipReason =
                                    "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain";
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
            await EventEmitter(_sessionIdGenerator()).EmitAsync(
                EventKind.GateFailure,
                parentTicket.Id,
                Phase.Chain,
                new Dictionary<string, object>
                {
                    ["kind"] = "batch_implement_unavailable",
                    ["reason"] = downgradeReason,
                    ["ticket_count"] = eligible.Count
                }, ct).ConfigureAwait(false);
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
                    environmentFailureDetected = childResult.ContainsEnvironmentalStop();
                    if (environmentFailureDetected)
                        environmentSkipReason = childResult.ContainsTicketingUnavailable()
                            ? "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain"
                            : "environment gate failure in a sibling; fix the environment once and re-run the chain";
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
                    var childIntegrationBranch = ChainIntegrationBranch.BranchName(child);
                    var childWorktreePath = await _integrationBranch.ResolveWorktreePathAsync(
                            childIntegrationBranch, child, ct)
                        .ConfigureAwait(false);
                    var accumulateFailure = await _integrationBranch.RebaseThenFastForwardAsync(
                        child.Id, childIntegrationBranch, childWorktreePath,
                        integrationBranch, sharedWorktreePath ?? integrationWorktreePath,
                        "chain_accumulate", () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
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

        // TLB-538/TLB-545: after an environmental stop, mark every undispatched child Skipped with
        // the reason. They were not failures and they were not silently dropped - the environment
        // must be fixed once, then a re-run picks them all up.
        if (environmentFailureDetected)
        {
            var dispatched = new HashSet<string>(allChildResults.Select(r => r.TicketId), StringComparer.Ordinal);
            foreach (var level in levels)
                foreach (var id in level)
                    if (!dispatched.Contains(id) && (batchedTicketIds is null || !batchedTicketIds.Contains(id)))
                        allChildResults.Add(new ChainResult(id, Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                            TimeSpan.Zero, null,
                            SkipReason: environmentSkipReason
                                ?? "environment gate failure in a sibling; fix the environment once and re-run the chain"));
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
            landingRationale = await _integrationBranch.LandRootIntegrationBranchAsync(
                options.TicketId, integrationBranch, sharedWorktreePath,
                _baseOptions.TargetBranch, () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
            if (landingRationale is not null)
                anyStoppedEarly = true;
        }

        // Attempt parent rollup (fail-soft)
        try { await _ticketing.RollupParentAsync(options.TicketId, ct).ConfigureAwait(false); }
        catch { /* non-fatal */ }

        totalSw.Stop();
        var outcome = anyStoppedEarly ? ChainOutcome.ParentStoppedEarly : ChainOutcome.ParentCompleted;
        var finalRationale = landingRationale
            ?? (environmentFailureDetected
                ? (childResults.Any(r => r.ContainsTicketingUnavailable())
                    ? $"Ticketing backend unreachable: {string.Join(", ", childResults.Where(r => r.ContainsTicketingUnavailable()).Select(r => r.TicketId))} stopped because the ticketing service could not be reached after transport retries; remaining children were skipped. Restore connectivity, then re-run."
                    : $"Environment gate failure: {string.Join(", ", childResults.Where(r => r.ContainsEnvironmentFailure()).Select(r => r.TicketId))} stopped because the gate also fails on the untouched base ref; remaining children were skipped. Fix the environment once, then re-run.")
                : anyStoppedEarly
                    ? $"One or more children did not complete: {string.Join(", ", childResults.Where(r => !IsChainSuccess(r.Outcome) && r.Outcome != ChainOutcome.Skipped).Select(r => r.TicketId))}"
                    : $"All {eligible.Count} eligible children completed.");

        if (options.ChainTargetBranch is null && IsChainSuccess(outcome))
            await _integrationBranch.SweepChainWorktreesAsync(
                options.TicketId, EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);

        return new ChainResult(
            TicketId: options.TicketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: totalSw.Elapsed,
            FinalRationale: finalRationale,
            ChildResults: childResults.AsReadOnly());
    }

    private static IReadOnlySet<string> AddVisited(IReadOnlySet<string>? visited, string uuid)
    {
        var next = visited is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(visited, StringComparer.Ordinal);
        next.Add(uuid);
        return next;
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

}
