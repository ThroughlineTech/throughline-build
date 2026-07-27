using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Owns the bounded implement, deterministic recheck, gate, and review loop.
/// </summary>
public sealed class ImplementReviewLoop
{
    private const int MaxReworkRounds = 2;
    private const int MaxCheckRetriesPerReworkRound = 2;

    private readonly ITicketing _ticketing;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase>
        _implementFactory;
    private readonly Func<BuildOptions, GateOutcome?, ReviewPhase> _reviewFactory;
    private readonly Func<BuildOptions, GatePhase>? _gateFactory;
    private readonly Func<BuildOptions, IObsoleteRatifier>? _ratifierFactory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly Func<string, ChainEventEmitter> _eventEmitterFactory;
    private readonly PhaseOptionsBuilder _phaseOptionsBuilder;
    private readonly BuildOptions _baseOptions;
    private readonly string _workingDirectory;
    private readonly IGitClient _git;
    private readonly IReadOnlyList<CheckSpec>? _reworkRecheckSpecs;
    private readonly AutomatedChecksRunner? _reworkRecheckRunner;

    public ImplementReviewLoop(
        ITicketing ticketing,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory,
        Func<BuildOptions, GatePhase>? gateFactory,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory,
        Func<string> sessionIdGenerator,
        Func<string, ChainEventEmitter> eventEmitterFactory,
        PhaseOptionsBuilder phaseOptionsBuilder,
        BuildOptions baseOptions,
        string workingDirectory,
        IGitClient git,
        IReadOnlyList<CheckSpec>? reworkRecheckSpecs,
        AutomatedChecksRunner? reworkRecheckRunner)
    {
        _ticketing = ticketing;
        _implementFactory = implementFactory;
        _reviewFactory = reviewFactory;
        _gateFactory = gateFactory;
        _ratifierFactory = ratifierFactory;
        _sessionIdGenerator = sessionIdGenerator;
        _eventEmitterFactory = eventEmitterFactory;
        _phaseOptionsBuilder = phaseOptionsBuilder;
        _baseOptions = baseOptions;
        _workingDirectory = workingDirectory;
        _git = git;
        _reworkRecheckSpecs = reworkRecheckSpecs;
        _reworkRecheckRunner = reworkRecheckRunner;
    }

    internal async Task<(
        ChainResult? abort,
        IReadOnlyList<string>? successProvides)> RunImplementReviewLoopAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int startRound,
        ReviewFeedback? initialFeedback,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        var round = startRound;
        var feedback = initialFeedback;
        var eventEmitter = _eventEmitterFactory(chainSessionId);
        string? priorCommitSha = null;
        long gateWallMs = 0;
        var gateAttributableReworkRounds = 0;
        long gateAttributableReworkInputTokens = 0;
        long gateAttributableReworkOutputTokens = 0;
        var gateAttributableReworkTokensTracked = false;
        var thisRoundIsGateAttributable = false;
        var gateWasEngaged = false;
        var checkRetriesThisRound = 0;
        var recheckRetryGateAttributable = false;

        async Task EmitCostLedgerAsync(int falseFails = 0)
        {
            if (!gateWasEngaged)
                return;
            await eventEmitter.EmitCostLedgerAsync(
                options.TicketId,
                gateWallMs,
                gateAttributableReworkRounds,
                gateAttributableReworkInputTokens,
                gateAttributableReworkOutputTokens,
                gateAttributableReworkTokensTracked,
                ct,
                falseFails).ConfigureAwait(false);
        }

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
                implSessionId,
                options.TicketId,
                "implement",
                round,
                options.ChainTargetBranch);
            var implChainRange =
                feedback is null ? options.ChainCommitRange : null;
            var implPhaseOpts = new ImplementPhaseOptions(
                feedback,
                options.SharedWorktreePath,
                implChainRange);
            eventEmitter.EmitPhaseStart(
                options,
                "implement",
                round,
                implSessionId);
            var implSw = Stopwatch.StartNew();
            var implResult = await _implementFactory(
                    implBuildOpts,
                    implPhaseOpts)
                .RunAsync(options.TicketId, _workingDirectory, ct)
                .ConfigureAwait(false);
            implSw.Stop();

            var implStep = new ChainStep(
                "implement",
                round,
                implResult.Success ? Status.Ok : Status.Failed,
                implResult.FailureReason,
                null,
                implSw.Elapsed,
                implSessionId);
            steps.Add(implStep);
            options.OnStep?.Invoke(options.TicketId, implStep);

            if (feedback is not null)
            {
                ReworkRoundManifest.Write(
                    implBuildOpts.DebugCaptureDirectory,
                    round,
                    feedback,
                    priorCommitSha,
                    implResult.CommitSha);
            }
            priorCommitSha = implResult.CommitSha;

            if (thisRoundIsGateAttributable)
            {
                gateAttributableReworkRounds++;
                var inputTokens = implResult.LlmInputTokens ?? 0L;
                var outputTokens = implResult.LlmOutputTokens ?? 0L;
                gateAttributableReworkInputTokens += inputTokens;
                gateAttributableReworkOutputTokens += outputTokens;
                if (inputTokens + outputTokens > 0)
                    gateAttributableReworkTokensTracked = true;
                thisRoundIsGateAttributable = false;
            }

            if (!implResult.Success)
            {
                if (!options.NoAutoResolve
                    && _ratifierFactory is not null
                    && implResult.EscalationWorkerResult is not null
                    && IsObsoleteEscalation(
                        implResult.EscalationWorkerResult))
                {
                    var ratifyVerdict = await RunRatificationAsync(
                        options,
                        steps,
                        implResult.EscalationWorkerResult,
                        implResult.WorktreePath,
                        ct).ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence = ExtractSubsumedByEvidence(
                            implResult.EscalationWorkerResult);
                        var finalRationale = FormatSubsumedRationale(evidence);
                        await _ticketing.TransitionAsync(
                            options.TicketId,
                            TicketState.Done,
                            ct).ConfigureAwait(false);
                        await eventEmitter.BestEffortTicketWriteAsync(
                            options.TicketId,
                            "subsumed_rationale_comment",
                            ticketing => ticketing.CreateCommentAsync(
                                options.TicketId,
                                "<p>" +
                                WebUtility.HtmlEncode(finalRationale) +
                                "</p>",
                                ct),
                            ct).ConfigureAwait(false);
                        await eventEmitter.EmitAsync(
                            EventKind.TicketSubsumed,
                            options.TicketId,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] =
                                    evidence?.Commit ?? "",
                                ["files"] =
                                    evidence?.Files.ToArray() ??
                                    Array.Empty<string>(),
                                ["rationale"] =
                                    evidence?.Rationale ?? ""
                            },
                            ct).ConfigureAwait(false);
                        totalSw.Stop();
                        return (
                            new ChainResult(
                                options.TicketId,
                                steps,
                                ChainOutcome.RatifiedObsolete,
                                totalSw.Elapsed,
                                finalRationale,
                                evidence),
                            null);
                    }
                }

                await EmitCostLedgerAsync().ConfigureAwait(false);
                return (
                    new ChainResult(
                        options.TicketId,
                        steps,
                        ChainOutcome.StoppedAtImplement,
                        TimeSpan.Zero,
                        null),
                    null);
            }

            if (feedback is not null
                && feedback.ChecksFailed.Count > 0
                && _reworkRecheckSpecs is { Count: > 0 }
                && _reworkRecheckRunner is not null)
            {
                var recheckWorktree =
                    implResult.WorktreePath ?? _workingDirectory;
                var recheckResults = new List<CheckResult>();
                foreach (var name in feedback.ChecksFailed.Distinct(
                             StringComparer.Ordinal))
                {
                    recheckResults.Add(
                        await _reworkRecheckRunner.RunNamedAsync(
                                name,
                                _reworkRecheckSpecs,
                                recheckWorktree,
                                ct)
                            .ConfigureAwait(false));
                }

                var stillFailing = recheckResults
                    .Where(result =>
                        !result.Skipped
                        && !result.Passed
                        && result.Role != CheckRole.Advisory)
                    .ToList();
                if (stillFailing.Count > 0)
                {
                    var stillFailingNames = stillFailing
                        .Select(result => result.Name)
                        .ToList();
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
                        },
                        ct).ConfigureAwait(false);

                    var recheckRationale =
                        "Post-rework re-run: the failing check(s) that " +
                        $"triggered rework round {round} STILL FAIL after " +
                        $"the changes: {string.Join(", ", stillFailingNames)}. " +
                        "The previous fix attempt did not satisfy the check; " +
                        "its verbatim output follows.";

                    if (checkRetriesThisRound
                        < MaxCheckRetriesPerReworkRound)
                    {
                        if (checkRetriesThisRound == 0)
                        {
                            recheckRetryGateAttributable =
                                feedback.GateFailedChecks is { Count: > 0 };
                        }
                        thisRoundIsGateAttributable =
                            recheckRetryGateAttributable;
                        checkRetriesThisRound++;
                        await _ticketing.TransitionAsync(
                            options.TicketId,
                            TicketState.InProgress,
                            ct).ConfigureAwait(false);
                        feedback = new ReviewFeedback(
                            recheckRationale,
                            stillFailingNames,
                            round,
                            FailedCheckDetails: stillFailing);
                        continue;
                    }

                    var failTail = string.Join(
                        "\n",
                        stillFailing.Select(result =>
                            $"- {result.Name} (exit {result.ExitCode}; " +
                            $"command: {result.CommandLine}): " +
                            (string.IsNullOrWhiteSpace(result.StderrTail)
                                ? result.StdoutTail.Trim()
                                : result.StderrTail.Trim())));
                    await EmitCostLedgerAsync().ConfigureAwait(false);
                    return (
                        new ChainResult(
                            options.TicketId,
                            steps,
                            ChainOutcome.ReworkCapExceeded,
                            TimeSpan.Zero,
                            recheckRationale + "\n" + failTail),
                        null);
                }

                checkRetriesThisRound = 0;
                recheckRetryGateAttributable = false;
            }

            GateOutcome? gateOutcome = null;
            if (_gateFactory is not null)
            {
                var gateSessionId = _sessionIdGenerator();
                var gateBuildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
                    gateSessionId,
                    options.TicketId,
                    "gate",
                    round,
                    options.ChainTargetBranch);
                eventEmitter.EmitPhaseStart(
                    options,
                    "gate",
                    round,
                    gateSessionId);
                var gateSw = Stopwatch.StartNew();
                var gateWorktreePath =
                    implResult.WorktreePath ?? _workingDirectory;
                var gateBranchName =
                    implResult.BranchName ??
                    PhaseWorktreeLayout.BranchName(options.TicketId);
                string gateBaseRef;
                try
                {
                    (gateBaseRef, _) = await BaseRefResolver.ResolveAsync(
                            _git,
                            _workingDirectory,
                            _baseOptions.TargetBranch,
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    gateBaseRef = _baseOptions.TargetBranch;
                }

                gateOutcome = await _gateFactory(gateBuildOpts).RunAsync(
                        options.TicketId,
                        gateWorktreePath,
                        gateBranchName,
                        gateBaseRef,
                        _workingDirectory,
                        implResult.CompletionClaim,
                        ct,
                        options.AccumulatedUpstreamProvides)
                    .ConfigureAwait(false);
                gateSw.Stop();
                var gateStep = new ChainStep(
                    "gate",
                    round,
                    gateOutcome.Passed ? Status.Ok : Status.Failed,
                    gateOutcome.HardFailReason,
                    null,
                    gateSw.Elapsed,
                    gateSessionId);
                steps.Add(gateStep);
                options.OnStep?.Invoke(options.TicketId, gateStep);
                gateWasEngaged = true;
                gateWallMs += gateOutcome.CheckResults.Sum(
                    result => (long)result.Elapsed.TotalMilliseconds);

                if (!gateOutcome.Passed)
                {
                    if (gateOutcome.Vacuous)
                    {
                        await EmitCostLedgerAsync().ConfigureAwait(false);
                        return (
                            new ChainResult(
                                options.TicketId,
                                steps,
                                ChainOutcome.GateVacuous,
                                TimeSpan.Zero,
                                gateOutcome.HardFailReason),
                            null);
                    }

                    if (gateOutcome.EnvironmentFailure)
                    {
                        var falseFails = gateOutcome.CheckResults.Count(
                            result =>
                                result.Role == CheckRole.Gating
                                && !result.Passed
                                && !result.Skipped);
                        await EmitCostLedgerAsync(
                                Math.Max(falseFails, 1))
                            .ConfigureAwait(false);
                        return (
                            new ChainResult(
                                options.TicketId,
                                steps,
                                ChainOutcome.GateEnvironmentFailure,
                                TimeSpan.Zero,
                                gateOutcome.HardFailReason),
                            null);
                    }

                    var gatingFailedResults = gateOutcome.CheckResults
                        .Where(result =>
                            result.Role == CheckRole.Gating
                            && !result.Passed
                            && !result.Skipped)
                        .ToList();
                    var gatingFailed = gatingFailedResults
                        .Select(result => result.Name)
                        .ToList();
                    var gateRationale =
                        gateOutcome.HardFailReason ??
                        "gate: gating checks failed";
                    if (round < MaxReworkRounds)
                    {
                        feedback = new ReviewFeedback(
                            gateRationale,
                            gatingFailed,
                            round + 1,
                            GateFailedChecks: gatingFailedResults);
                        await EmitReworkRoundAsync(
                                eventEmitter,
                                options.TicketId,
                                round + 1,
                                "GateFailure",
                                gateRationale,
                                ct)
                            .ConfigureAwait(false);
                        thisRoundIsGateAttributable = true;
                        round++;
                        continue;
                    }

                    await EmitCostLedgerAsync().ConfigureAwait(false);
                    return (
                        new ChainResult(
                            options.TicketId,
                            steps,
                            ChainOutcome.ReworkCapExceeded,
                            TimeSpan.Zero,
                            gateRationale),
                        null);
                }
            }

            var reviewResult = await RunOneReviewAsync(
                    options,
                    steps,
                    round,
                    gateOutcome,
                    ct)
                .ConfigureAwait(false);
            if (reviewResult.abort is not null)
            {
                await EmitCostLedgerAsync().ConfigureAwait(false);
                return (reviewResult.abort, null);
            }

            var verdict = reviewResult.verdict!;
            if (verdict.Kind == VerdictKind.Pass)
            {
                await EmitCostLedgerAsync().ConfigureAwait(false);
                return (null, implResult.CompletionClaim?.Provides);
            }
            if (verdict.Kind == VerdictKind.Fail)
            {
                await EmitCostLedgerAsync().ConfigureAwait(false);
                return (
                    new ChainResult(
                        options.TicketId,
                        steps,
                        ChainOutcome.StoppedAtReview,
                        TimeSpan.Zero,
                        verdict.Rationale),
                    null);
            }

            if (round < MaxReworkRounds)
            {
                feedback = new ReviewFeedback(
                    verdict.Rationale,
                    verdict.ChecksFailed,
                    round + 1,
                    FailedCheckDetails: MatchFailedCheckDetails(
                        verdict.ChecksFailed,
                        reviewResult.checkResults));
                await EmitReworkRoundAsync(
                        eventEmitter,
                        options.TicketId,
                        round + 1,
                        "Rework",
                        verdict.Rationale,
                        ct)
                    .ConfigureAwait(false);
                round++;
                continue;
            }

            await EmitCostLedgerAsync().ConfigureAwait(false);
            return (
                new ChainResult(
                    options.TicketId,
                    steps,
                    ChainOutcome.ReworkCapExceeded,
                    TimeSpan.Zero,
                    verdict.Rationale),
                null);
        }
    }

    internal async Task<(
        ChainResult? abort,
        IReadOnlyList<string>? successProvides)> RunReviewBranchAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int round,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        var reviewResult = await RunOneReviewAsync(
                options,
                steps,
                round,
                null,
                ct)
            .ConfigureAwait(false);
        if (reviewResult.abort is not null)
            return (reviewResult.abort, null);

        var verdict = reviewResult.verdict!;
        if (verdict.Kind == VerdictKind.Pass)
            return (null, null);
        if (verdict.Kind == VerdictKind.Fail)
        {
            return (
                new ChainResult(
                    options.TicketId,
                    steps,
                    ChainOutcome.StoppedAtReview,
                    TimeSpan.Zero,
                    verdict.Rationale),
                null);
        }

        var feedback = new ReviewFeedback(
            verdict.Rationale,
            verdict.ChecksFailed,
            round + 1,
            FailedCheckDetails: MatchFailedCheckDetails(
                verdict.ChecksFailed,
                reviewResult.checkResults));
        await EmitReworkRoundAsync(
                _eventEmitterFactory(chainSessionId),
                options.TicketId,
                round + 1,
                "Rework",
                verdict.Rationale,
                ct)
            .ConfigureAwait(false);
        return await RunImplementReviewLoopAsync(
                options,
                steps,
                chainSessionId,
                round + 1,
                feedback,
                totalSw,
                ct)
            .ConfigureAwait(false);
    }

    internal async Task<(
        ChainResult? abort,
        Verdict? verdict,
        IReadOnlyList<CheckResult>? checkResults)> RunOneReviewAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        int round,
        GateOutcome? gateOutcome,
        CancellationToken ct)
    {
        var revSessionId = _sessionIdGenerator();
        var revBuildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
            revSessionId,
            options.TicketId,
            "review",
            round,
            options.ChainTargetBranch);
        _eventEmitterFactory(revSessionId).EmitPhaseStart(
            options,
            "review",
            -1,
            revSessionId);
        var revSw = Stopwatch.StartNew();
        var revResult = await _reviewFactory(revBuildOpts, gateOutcome)
            .RunAsync(options.TicketId, _workingDirectory, ct)
            .ConfigureAwait(false);
        revSw.Stop();

        if (!revResult.Success)
        {
            var failedStep = new ChainStep(
                "review",
                -1,
                Status.Failed,
                revResult.FailureReason,
                null,
                revSw.Elapsed,
                revSessionId);
            steps.Add(failedStep);
            options.OnStep?.Invoke(options.TicketId, failedStep);
            var outcome = revResult.ProviderUnavailable is not null
                ? ChainOutcome.ReviewUnavailable
                : ChainOutcome.StoppedAtReview;
            return (
                new ChainResult(
                    options.TicketId,
                    steps,
                    outcome,
                    TimeSpan.Zero,
                    revResult.FailureReason),
                null,
                null);
        }

        var step = new ChainStep(
            "review",
            -1,
            Status.Ok,
            null,
            revResult.Verdict,
            revSw.Elapsed,
            revSessionId);
        steps.Add(step);
        options.OnStep?.Invoke(options.TicketId, step);
        return (
            null,
            new Verdict(
                revResult.Verdict!.Value,
                revResult.VerdictRationale ?? "",
                revResult.ChecksFailed),
            revResult.CheckResults);
    }

    internal static IReadOnlyList<CheckResult>? MatchFailedCheckDetails(
        IReadOnlyList<string> checksFailed,
        IReadOnlyList<CheckResult>? checkResults)
    {
        if (checksFailed.Count == 0 || checkResults is null)
            return null;
        var names = new HashSet<string>(
            checksFailed,
            StringComparer.Ordinal);
        var details = checkResults
            .Where(result =>
                names.Contains(result.Name)
                && !result.Passed
                && !result.Skipped)
            .ToList();
        return details.Count > 0 ? details : null;
    }

    internal static string FormatSubsumedRationale(
        SubsumedByEvidence? evidence)
    {
        var commit = evidence?.Commit ?? "(unknown)";
        var rationale = evidence?.Rationale ?? "(no rationale)";
        var files = evidence?.Files is { Count: > 0 } found
            ? string.Join(", ", found)
            : "(none)";
        return $"Subsumed by {commit}: {rationale}; files: {files}";
    }

    internal static string? RationalePreview(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return null;
        return rationale.Length <= 200
            ? rationale
            : rationale.Substring(0, 200);
    }

    internal static bool IsObsoleteEscalation(WorkerResult result)
    {
        if (result.Status != Status.Escalate
            || !result.Metadata.TryGetValue(
                "escalation",
                out var escalationObject)
            || escalationObject is not JsonElement escalation
            || escalation.ValueKind != JsonValueKind.Object
            || !escalation.TryGetProperty("reason", out var reason)
            || reason.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return string.Equals(
            reason.GetString(),
            "obsolete",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static SubsumedByEvidence? ExtractSubsumedByEvidence(
        WorkerResult result)
    {
        if (!result.Metadata.TryGetValue(
                "escalation",
                out var escalationObject)
            || escalationObject is not JsonElement escalation
            || escalation.ValueKind != JsonValueKind.Object
            || !escalation.TryGetProperty(
                "subsumed_by",
                out var subsumedBy)
            || subsumedBy.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var commit =
            subsumedBy.TryGetProperty("commit", out var commitElement)
            && commitElement.ValueKind == JsonValueKind.String
                ? commitElement.GetString()
                : null;
        var rationale =
            subsumedBy.TryGetProperty("rationale", out var rationaleElement)
            && rationaleElement.ValueKind == JsonValueKind.String
                ? rationaleElement.GetString()
                : null;
        if (string.IsNullOrEmpty(commit)
            || string.IsNullOrEmpty(rationale))
        {
            return null;
        }

        var files = new List<string>();
        if (subsumedBy.TryGetProperty("files", out var filesElement)
            && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.String)
                    continue;
                var file = fileElement.GetString();
                if (!string.IsNullOrEmpty(file))
                    files.Add(file);
            }
        }
        return new SubsumedByEvidence(
            commit,
            files.AsReadOnly(),
            rationale);
    }

    internal async Task<Verdict> RunRatificationAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        WorkerResult escalateResult,
        string? evidenceDirectory,
        CancellationToken ct)
    {
        var sessionId = _sessionIdGenerator();
        var buildOpts = _phaseOptionsBuilder.BuildPhaseOptions(
            sessionId,
            options.TicketId,
            "ratify");
        var ratifier = _ratifierFactory!(buildOpts);
        var ticket = await _ticketing.GetAsync(options.TicketId, ct)
            .ConfigureAwait(false);
        _eventEmitterFactory(sessionId).EmitPhaseStart(
            options,
            "ratify",
            -1,
            sessionId);
        var stopwatch = Stopwatch.StartNew();
        var verdict = await ratifier.RatifyAsync(
                ticket,
                escalateResult,
                evidenceDirectory,
                ct)
            .ConfigureAwait(false);
        stopwatch.Stop();
        var step = new ChainStep(
            "ratify",
            -1,
            verdict.Kind == VerdictKind.Pass
                ? Status.Ok
                : Status.Failed,
            verdict.Kind != VerdictKind.Pass
                ? verdict.Rationale
                : null,
            verdict.Kind,
            stopwatch.Elapsed,
            sessionId);
        steps.Add(step);
        options.OnStep?.Invoke(options.TicketId, step);
        return verdict;
    }

    private static Task EmitReworkRoundAsync(
        ChainEventEmitter eventEmitter,
        string ticketId,
        int round,
        string trigger,
        string rationale,
        CancellationToken ct) =>
        eventEmitter.EmitAsync(
            EventKind.ReworkRound,
            ticketId,
            Phase.Implement,
            new Dictionary<string, object>
            {
                ["round"] = round,
                ["verdict_that_triggered"] = trigger,
                ["rationale_preview"] = RationalePreview(rationale) ?? ""
            },
            ct);
}
