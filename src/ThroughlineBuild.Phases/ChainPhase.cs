using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

public record ChainPhaseOptions(string TicketId, bool Debug);

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

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        var startPhase = ticket.State switch
        {
            TicketState.Backlog => StartPhase.Plan,
            TicketState.Ready => StartPhase.Implement,
            TicketState.InReview => StartPhase.Review,
            _ => StartPhase.Refused
        };

        if (startPhase == StartPhase.Refused)
        {
            totalSw.Stop();
            return new ChainResult(options.TicketId, steps, ChainOutcome.RefusedInitialState,
                totalSw.Elapsed, null);
        }

        if (startPhase == StartPhase.Plan)
        {
            var sessionId = _sessionIdGenerator();
            var buildOpts = _baseOptions with { SessionId = sessionId };
            var sw = Stopwatch.StartNew();
            var planResult = await _planFactory(buildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
                .ConfigureAwait(false);
            sw.Stop();

            steps.Add(new ChainStep(
                PhaseName: "plan",
                ReworkRoundNumber: -1,
                Status: planResult.Success ? Status.Ok : Status.Failed,
                FailureReason: planResult.FailureReason,
                Verdict: null,
                Duration: sw.Elapsed,
                PhaseSessionId: sessionId));

            if (!planResult.Success)
            {
                totalSw.Stop();
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtPlan,
                    totalSw.Elapsed, null);
            }
        }

        if (startPhase == StartPhase.Plan || startPhase == StartPhase.Implement)
        {
            var chainResult = await RunImplementReviewLoopAsync(options, steps, 0, null, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                return chainResult with { TotalDuration = totalSw.Elapsed };
            }
        }
        else
        {
            var chainResult = await RunReviewBranchAsync(options, steps, 0, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                return chainResult with { TotalDuration = totalSw.Elapsed };
            }
        }

        var shipSessionId = _sessionIdGenerator();
        var shipBuildOpts = _baseOptions with { SessionId = shipSessionId };
        var shipSw = Stopwatch.StartNew();
        var shipResult = await _shipFactory(shipBuildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
            .ConfigureAwait(false);
        shipSw.Stop();

        steps.Add(new ChainStep(
            PhaseName: "ship",
            ReworkRoundNumber: -1,
            Status: shipResult.Success ? Status.Ok : Status.Failed,
            FailureReason: shipResult.FailureReason,
            Verdict: null,
            Duration: shipSw.Elapsed,
            PhaseSessionId: shipSessionId));

        totalSw.Stop();
        if (!shipResult.Success)
            return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtShip,
                totalSw.Elapsed, null);

        return new ChainResult(options.TicketId, steps, ChainOutcome.Completed, totalSw.Elapsed, null);
    }

    private async Task<ChainResult?> RunImplementReviewLoopAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
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

            steps.Add(new ChainStep(
                PhaseName: "implement",
                ReworkRoundNumber: round,
                Status: implResult.Success ? Status.Ok : Status.Failed,
                FailureReason: implResult.FailureReason,
                Verdict: null,
                Duration: implSw.Elapsed,
                PhaseSessionId: implSessionId));

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
        return await RunImplementReviewLoopAsync(options, steps, round + 1, feedback, ct)
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
            steps.Add(new ChainStep(
                PhaseName: "review",
                ReworkRoundNumber: -1,
                Status: Status.Failed,
                FailureReason: revResult.FailureReason,
                Verdict: null,
                Duration: revSw.Elapsed,
                PhaseSessionId: revSessionId));
            return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, revResult.FailureReason), null);
        }

        steps.Add(new ChainStep(
            PhaseName: "review",
            ReworkRoundNumber: -1,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: revResult.Verdict,
            Duration: revSw.Elapsed,
            PhaseSessionId: revSessionId));

        return (null, new Verdict(revResult.Verdict!.Value, revResult.VerdictRationale ?? "", revResult.ChecksFailed));
    }

    private enum StartPhase { Plan, Implement, Review, Refused }
}
