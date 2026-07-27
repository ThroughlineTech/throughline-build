using System.Diagnostics;
using System.Net;
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
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
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
    private readonly PhaseOptionsBuilder _phaseOptionsBuilder;
    private readonly ImplementReviewLoop _implementReviewLoop;
    private readonly ParentChainRunner _parentChainRunner;
    // Optional: recovers the latest Rework verdict from the event log so an in-progress ticket
    // that carries real work can be resumed with its prior feedback. Null falls back to a
    // synthesized resume note (e.g. an interrupted initial implement that was never reviewed).
    private readonly IReviewFeedbackRetriever? _feedbackRetriever;
    // Optional: when set, batch implement groups in the parent chain dispatch one session here
    // instead of running a per-ticket implement+review+ship loop for each group member.
    private readonly IWorkerAgent? _batchWorker;
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
        _phaseOptionsBuilder = new PhaseOptionsBuilder(_baseOptions);
        _batchImplementRunner = new BatchImplementRunner(
            batchWorker,
            _ticketing,
            _git,
            _baseOptions,
            _workingDirectory,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            _planFactory,
            _phaseOptionsBuilder);
        _batchReviewRunner = new BatchReviewRunner(
            batchWorker,
            _ticketing,
            _git,
            _baseOptions,
            _workingDirectory,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            implementFactory,
            _phaseOptionsBuilder);
        _implementReviewLoop = new ImplementReviewLoop(
            _ticketing,
            implementFactory,
            reviewFactory,
            gateFactory,
            _ratifierFactory,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            _phaseOptionsBuilder,
            _baseOptions,
            _workingDirectory,
            _git,
            reworkRecheckSpecs,
            reworkRecheckRunner);
        _parentChainRunner = new ParentChainRunner(
            _ticketing,
            _baseOptions,
            _batchWorker,
            _batchImplementRunner,
            _batchReviewRunner,
            _git,
            _integrationBranch,
            _sessionIdGenerator,
            sessionId => EventEmitter(sessionId),
            (childOptions, childCt) => RunAsync(childOptions, childCt),
            _workingDirectory,
            output);
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

            return await _parentChainRunner
                .RunAsync(options, ticket, chainChildren, ct)
                .ConfigureAwait(false);
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
            var buildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
                sessionId,
                options.TicketId,
                "plan",
                targetBranch: options.ChainTargetBranch);
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
                    ImplementReviewLoop.IsObsoleteEscalation(
                        planResult.EscalationWorkerResult))
                {
                    // Plan runs in the main working directory (no worktree exists yet), so its
                    // obsolete evidence resolves there; null defers to the ratifier's own dir.
                    var ratifyVerdict = await _implementReviewLoop
                        .RunRatificationAsync(
                            options,
                            steps,
                            planResult.EscalationWorkerResult,
                            evidenceDirectory: null,
                            ct)
                        .ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence =
                            ImplementReviewLoop.ExtractSubsumedByEvidence(
                                planResult.EscalationWorkerResult);
                        var finalRationale =
                            ImplementReviewLoop.FormatSubsumedRationale(
                                evidence);
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
            var (loopFailure, loopProvides) = await _implementReviewLoop
                .RunImplementReviewLoopAsync(
                    options,
                    steps,
                    chainSessionId,
                    startRound,
                    initialFeedback,
                    totalSw,
                    ct)
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
            var (loopFailure, loopProvides) = await _implementReviewLoop
                .RunReviewBranchAsync(
                    options,
                    steps,
                    chainSessionId,
                    0,
                    totalSw,
                    ct)
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
        var shipBuildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
            shipSessionId,
            options.TicketId,
            "ship",
            targetBranch: options.ChainTargetBranch);
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

}
