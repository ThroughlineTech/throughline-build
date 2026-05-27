using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

public record ChainPhaseOptions(string TicketId, bool Debug, Action<ChainStep>? OnStep = null);

public class ChainPhase
{
    private const int MaxReworkRounds = 2;

    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> _implementFactory;
    private readonly Func<BuildOptions, ReviewPhase> _reviewFactory;
    private readonly Func<BuildOptions, ShipPhase> _shipFactory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly string _workingDirectory;
    private readonly BuildOptions _baseOptions;

    public ChainPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions baseOptions,
        Func<BuildOptions, PlanPhase> planFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<string>? sessionIdGenerator = null,
        string? workingDirectory = null)
    {
        _ticketing = ticketing;
        _events = events;
        _baseOptions = baseOptions;
        _planFactory = planFactory;
        _implementFactory = implementFactory;
        _reviewFactory = reviewFactory;
        _shipFactory = shipFactory;
        _sessionIdGenerator = sessionIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    }

    public async Task<ChainResult> RunAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var steps = new List<ChainStep>();

        var chainSessionId = _sessionIdGenerator();

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        var startPhase = ticket.State switch
        {
            TicketState.Backlog => StartPhase.Plan,
            TicketState.Ready => StartPhase.Implement,
            TicketState.InReview => StartPhase.Review,
            _ => StartPhase.Refused
        };

        var startingAtPhaseStr = startPhase switch
        {
            StartPhase.Plan => "plan",
            StartPhase.Implement => "implement",
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
            var buildOpts = _baseOptions with { SessionId = sessionId };
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
            options.OnStep?.Invoke(planStep);

            if (!planResult.Success)
            {
                totalSw.Stop();
                var stoppedAtPlan = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtPlan,
                    totalSw.Elapsed, null);
                await EmitChainEndAsync(stoppedAtPlan, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return stoppedAtPlan;
            }
        }

        if (startPhase == StartPhase.Plan || startPhase == StartPhase.Implement)
        {
            var chainResult = await RunImplementReviewLoopAsync(options, steps, chainSessionId, 0, null, ct)
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
            var chainResult = await RunReviewBranchAsync(options, steps, chainSessionId, 0, ct)
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
        var shipBuildOpts = _baseOptions with { SessionId = shipSessionId };
        var shipSw = Stopwatch.StartNew();
        var shipResult = await _shipFactory(shipBuildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
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
        options.OnStep?.Invoke(shipStep);

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
        CancellationToken ct)
    {
        int round = startRound;
        ReviewFeedback? feedback = initialFeedback;

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = _baseOptions with { SessionId = implSessionId };
            var implPhaseOpts = new ImplementPhaseOptions(feedback);
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
            options.OnStep?.Invoke(implStep);

            if (!implResult.Success)
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtImplement,
                    TimeSpan.Zero, null);

            var reviewResult = await RunOneReviewAsync(options, steps, ct).ConfigureAwait(false);

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
        CancellationToken ct)
    {
        var reviewResult = await RunOneReviewAsync(options, steps, ct).ConfigureAwait(false);

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
        return await RunImplementReviewLoopAsync(options, steps, chainSessionId, round + 1, feedback, ct)
            .ConfigureAwait(false);
    }

    private async Task<(ChainResult? abort, Verdict? verdict)> RunOneReviewAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        CancellationToken ct)
    {
        var revSessionId = _sessionIdGenerator();
        var revBuildOpts = _baseOptions with { SessionId = revSessionId };
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
            options.OnStep?.Invoke(failedRevStep);
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
        options.OnStep?.Invoke(revStep);

        return (null, new Verdict(revResult.Verdict!.Value, revResult.VerdictRationale ?? "", revResult.ChecksFailed));
    }

    private static string? RationalePreview(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return null;
        return rationale.Length <= 200 ? rationale : rationale.Substring(0, 200);
    }

    private enum StartPhase { Plan, Implement, Review, Refused }
}
