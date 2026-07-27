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
/// Runs combined batch review passes and routes bounded rework through either the affected ticket
/// or the full batch stack.
/// </summary>
public sealed class BatchReviewRunner
{
    private const int MaxReworkRounds = 2;

    private readonly IWorkerAgent? _batchWorker;
    private readonly ITicketing _ticketing;
    private readonly IGitClient _git;
    private readonly BuildOptions _baseOptions;
    private readonly string _workingDirectory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly Func<string, ChainEventEmitter> _eventEmitterFactory;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> _implementFactory;
    private readonly PhaseOptionsBuilder _phaseOptionsBuilder;

    public BatchReviewRunner(
        IWorkerAgent? batchWorker,
        ITicketing ticketing,
        IGitClient git,
        BuildOptions baseOptions,
        string workingDirectory,
        Func<string> sessionIdGenerator,
        Func<string, ChainEventEmitter> eventEmitterFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        PhaseOptionsBuilder phaseOptionsBuilder)
    {
        _batchWorker = batchWorker;
        _ticketing = ticketing;
        _git = git;
        _baseOptions = baseOptions;
        _workingDirectory = workingDirectory;
        _sessionIdGenerator = sessionIdGenerator;
        _eventEmitterFactory = eventEmitterFactory;
        _implementFactory = implementFactory;
        _phaseOptionsBuilder = phaseOptionsBuilder;
    }

    internal enum BatchReworkRoute
    {
        Localized,
        CrossTicket
    }

    internal async Task<bool> RunBatchReviewAndReworkAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string batchBranchName,
        string baseRef,
        string sharedWorktreePath,
        string? chainStartSha,
        CancellationToken ct)
    {
        var currentConfirmedTickets = confirmedTickets;
        var outcome = await RunCombinedBatchReviewAsync(
            batchTickets,
            currentConfirmedTickets,
            batchBranchName,
            baseRef,
            sharedWorktreePath,
            _sessionIdGenerator(),
            ct).ConfigureAwait(false);

        if (outcome.Passed)
            return true;
        if (outcome.FinalVerdict == VerdictKind.Fail)
            return false;

        for (var reworkRound = 1; reworkRound <= MaxReworkRounds; reworkRound++)
        {
            var feedback = new ReviewFeedback(
                outcome.Rationale, outcome.ChecksFailed, reworkRound);
            var route = ClassifyBatchRework(batchTickets, outcome.Rationale);

            if (route == BatchReworkRoute.Localized)
            {
                var targetTicket = batchTickets.First(ticket =>
                    outcome.Rationale.Contains(ticket.Id, StringComparison.Ordinal));
                var reworkSucceeded = await RunLocalizedBatchReworkAsync(
                    targetTicket.Id,
                    feedback,
                    sharedWorktreePath,
                    reworkRound,
                    ct).ConfigureAwait(false);
                if (!reworkSucceeded)
                    return false;
            }
            else
            {
                var newConfirmed = await RunCrossTicketBatchReworkAsync(
                    batchTickets,
                    batchBranchName,
                    baseRef,
                    sharedWorktreePath,
                    chainStartSha,
                    feedback,
                    ct).ConfigureAwait(false);
                if (newConfirmed is null)
                    return false;
                currentConfirmedTickets = newConfirmed;
            }

            outcome = await RunCombinedBatchReviewAsync(
                batchTickets,
                currentConfirmedTickets,
                batchBranchName,
                baseRef,
                sharedWorktreePath,
                _sessionIdGenerator(),
                ct).ConfigureAwait(false);

            if (outcome.Passed)
                return true;
            if (outcome.FinalVerdict == VerdictKind.Fail)
                return false;
        }

        return false;
    }

    internal async Task<BatchReviewOutcome> RunCombinedBatchReviewAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string batchBranchName,
        string baseRef,
        string sharedWorktreePath,
        string chainSessionId,
        CancellationToken ct)
    {
        var primaryTicketId = batchTickets[0].Id;
        var sizeExceedsThreshold =
            batchTickets.Count > _baseOptions.BatchReviewSizeThreshold;

        GitDiff combinedDiff;
        try
        {
            combinedDiff = await _git.DiffAsync(
                baseRef,
                batchBranchName,
                _workingDirectory,
                includePatchContent: true,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _eventEmitterFactory(chainSessionId).EmitAsync(
                EventKind.GateFailure,
                primaryTicketId,
                Phase.Review,
                new Dictionary<string, object>
                {
                    ["kind"] = "batch_review_diff_failed",
                    ["error"] = ex.Message
                },
                ct).ConfigureAwait(false);
            return new BatchReviewOutcome(
                false,
                VerdictKind.Fail,
                $"diff failed: {ex.Message}",
                Array.Empty<string>());
        }

        var (pass1Verdict, pass1Rationale, pass1Checks) =
            await RunOneBatchReviewPassAsync(
                batchTickets,
                confirmedTickets,
                baseRef,
                combinedDiff,
                sharedWorktreePath,
                chainSessionId,
                pass: 1,
                ct).ConfigureAwait(false);
        await PostBatchReviewCommentAsync(
            primaryTicketId,
            1,
            pass1Verdict,
            pass1Rationale,
            pass1Checks,
            ct).ConfigureAwait(false);

        if (pass1Verdict == VerdictKind.Fail)
        {
            return new BatchReviewOutcome(
                false, VerdictKind.Fail, pass1Rationale, pass1Checks);
        }

        var needSecondPass =
            pass1Verdict == VerdictKind.Rework || sizeExceedsThreshold;
        if (!needSecondPass)
        {
            return new BatchReviewOutcome(
                true, VerdictKind.Pass, pass1Rationale, pass1Checks);
        }

        var (pass2Verdict, pass2Rationale, pass2Checks) =
            await RunOneBatchReviewPassAsync(
                batchTickets,
                confirmedTickets,
                baseRef,
                combinedDiff,
                sharedWorktreePath,
                chainSessionId,
                pass: 2,
                ct).ConfigureAwait(false);
        await PostBatchReviewCommentAsync(
            primaryTicketId,
            2,
            pass2Verdict,
            pass2Rationale,
            pass2Checks,
            ct).ConfigureAwait(false);

        return new BatchReviewOutcome(
            pass2Verdict == VerdictKind.Pass,
            pass2Verdict,
            pass2Rationale,
            pass2Checks);
    }

    private async Task<(
        VerdictKind Verdict,
        string Rationale,
        IReadOnlyList<string> ChecksFailed)> RunOneBatchReviewPassAsync(
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

        await _eventEmitterFactory(reviewSessionId).EmitAsync(
            EventKind.WorkerSpawn,
            primaryTicketId,
            Phase.Review,
            new Dictionary<string, object>
            {
                ["worker"] = _batchWorker!.Name,
                ["role"] = "batch_verifier",
                ["pass"] = pass
            },
            ct).ConfigureAwait(false);

        var reviewBrief = BatchReviewBriefBuilder.Build(
            _batchWorker.Name,
            batchTickets,
            confirmedTickets,
            baseRef,
            combinedDiff,
            checkResults: Array.Empty<CheckResult>());
        var workerOptions = new WorkerOptions(
            _baseOptions.WorkerTimeout,
            _baseOptions.WorkerAllowedTools,
            DebugCaptureDirectory: _baseOptions.DebugCaptureDirectory,
            LiveStdoutSink: _baseOptions.LiveStdoutSink,
            LiveStderrSink: _baseOptions.LiveStderrSink,
            ProgressDigestSink: _baseOptions.ProgressDigestSink,
            Size: batchTickets.Max(ticket =>
                WorkerSizeMapper.FromTicketSize(ticket.Size)),
            DebugTranscript: new DebugTranscriptContext(
                BuildVersion: _baseOptions.BuildVersion,
                SessionId: reviewSessionId));

        var workerResult = await _batchWorker.ExecuteAsync(
            reviewBrief, sharedWorktreePath, workerOptions, ct)
            .ConfigureAwait(false);

        await _eventEmitterFactory(reviewSessionId).EmitAsync(
            EventKind.VerifierVerdict,
            primaryTicketId,
            Phase.Review,
            new Dictionary<string, object>
            {
                ["worker_status"] = workerResult.Status.ToString(),
                ["pass"] = pass
            },
            ct).ConfigureAwait(false);

        if (workerResult.Status != Status.Ok)
        {
            var reason =
                workerResult.FailureReason ?? workerResult.Status.ToString();
            return (
                VerdictKind.Fail,
                $"batch review worker failed (pass {pass}): {reason}",
                Array.Empty<string>());
        }

        var metadata = workerResult.Metadata;
        var verdictRaw = TryGetBatchReviewMetadataString(metadata, "verdict");
        var verdict = string.Equals(
            verdictRaw, "Pass", StringComparison.OrdinalIgnoreCase)
            ? VerdictKind.Pass
            : string.Equals(
                verdictRaw, "Rework", StringComparison.OrdinalIgnoreCase)
                ? VerdictKind.Rework
                : VerdictKind.Fail;

        var blocks = workerResult.Blocks ?? new Dictionary<string, string>();
        var rationale = FencedBlockResolver.TryResolveRef(
            blocks,
            metadata,
            "rationale_ref",
            out var resolvedRationale,
            out _)
            && resolvedRationale is not null
                ? resolvedRationale
                : TryGetBatchReviewMetadataString(metadata, "rationale") ?? "";
        return (verdict, rationale, ParseBatchReviewChecksFailed(metadata));
    }

    internal async Task PostBatchReviewCommentAsync(
        string ticketId,
        int pass,
        VerdictKind verdict,
        string rationale,
        IReadOnlyList<string> checksFailed,
        CancellationToken ct)
    {
        var checksNote = checksFailed.Count > 0
            ? $" checks_failed: {string.Join(", ", checksFailed)}"
            : "";
        var passNote = pass > 1 ? $" (pass {pass})" : "";
        var commentHtml =
            $"<p>[batch_review{passNote}: {verdict}]{checksNote}</p>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(rationale)}</p>";
        await _eventEmitterFactory(_sessionIdGenerator()).BestEffortTicketWriteAsync(
            ticketId,
            "batch_review_comment",
            ticketing => ticketing.CreateCommentAsync(ticketId, commentHtml, ct),
            ct).ConfigureAwait(false);
    }

    internal static string? TryGetBatchReviewMetadataString(
        IReadOnlyDictionary<string, object> metadata,
        string key)
    {
        try
        {
            if (!metadata.TryGetValue(key, out var value))
                return null;
            if (value is string text)
                return text;
            if (value is JsonElement element
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> ParseBatchReviewChecksFailed(
        IReadOnlyDictionary<string, object> metadata)
    {
        try
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
                    if (item is string text)
                        result.Add(text);
                    else if (item is JsonElement element
                        && element.ValueKind == JsonValueKind.String)
                    {
                        result.Add(element.GetString() ?? "");
                    }
                }
                return result;
            }
            if (raw is JsonElement arrayElement
                && arrayElement.ValueKind == JsonValueKind.Array)
            {
                var result = new List<string>();
                foreach (var element in arrayElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        result.Add(element.GetString() ?? "");
                }
                return result;
            }
            return Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    internal static BatchReworkRoute ClassifyBatchRework(
        IReadOnlyList<Ticket> batchTickets,
        string rationale)
    {
        var count = 0;
        foreach (var ticket in batchTickets)
        {
            if (rationale.Contains(ticket.Id, StringComparison.Ordinal))
                count++;
            if (count > 1)
                return BatchReworkRoute.CrossTicket;
        }
        return count == 1
            ? BatchReworkRoute.Localized
            : BatchReworkRoute.CrossTicket;
    }

    private async Task<bool> RunLocalizedBatchReworkAsync(
        string targetTicketId,
        ReviewFeedback feedback,
        string sharedWorktreePath,
        int reworkRound,
        CancellationToken ct)
    {
        await BatchTicketWriter.RunBatchStateWriteAsync(
            targetTicketId,
            () => _ticketing.TransitionAsync(
                targetTicketId, TicketState.InProgress, ct)).ConfigureAwait(false);

        var sessionId = _sessionIdGenerator();
        var buildOptions = _phaseOptionsBuilder.BuildPhaseOptions(
            sessionId,
            targetTicketId,
            "batch-rework-localized",
            null,
            null);
        var implementPhase = _implementFactory(
            buildOptions,
            new ImplementPhaseOptions(
                ReviewFeedback: feedback,
                SharedWorktreePath: sharedWorktreePath));
        try
        {
            var result = await implementPhase.RunAsync(
                targetTicketId, _workingDirectory, ct).ConfigureAwait(false);
            return result.Success;
        }
        catch (TicketingUnavailableException ex)
        {
            throw new BatchTicketingUnavailableException(targetTicketId, ex);
        }
    }

    private async Task<IReadOnlyList<BatchTicketResult>?>
        RunCrossTicketBatchReworkAsync(
            IReadOnlyList<Ticket> batchTickets,
            string batchBranchName,
            string baseRef,
            string sharedWorktreePath,
            string? chainStartSha,
            ReviewFeedback feedback,
            CancellationToken ct)
    {
        foreach (var ticket in batchTickets)
        {
            await BatchTicketWriter.RunBatchStateWriteAsync(
                ticket.Id,
                () => _ticketing.TransitionAsync(
                    ticket.Id, TicketState.InProgress, ct)).ConfigureAwait(false);
        }

        var batchCommitRange = await ComputeBatchCommitRangeAsync(chainStartSha, ct)
            .ConfigureAwait(false);
        var mainSha = await ResolveMainShaAsync(baseRef, ct).ConfigureAwait(false);
        var repoState = new RepoState(
            mainSha,
            Directory.EnumerateFileSystemEntries(_workingDirectory)
                .ToList()
                .AsReadOnly());

        var reworkSessionId = _sessionIdGenerator();
        var firstTicket = batchTickets[0];
        var batchBuildOptions = _phaseOptionsBuilder.BuildPhaseOptions(
            reworkSessionId,
            firstTicket.Id,
            "batch-rework-cross",
            null,
            null);
        var batchBrief = BatchImplementBriefBuilder.Build(
            _batchWorker!.Name,
            batchTickets,
            repoState,
            batchBranchName,
            sharedWorktreePath,
            batchCommitRange,
            reworkFeedback: feedback);
        var workerOptions = new WorkerOptions(
            _baseOptions.WorkerTimeout,
            _baseOptions.WorkerAllowedTools,
            DebugCaptureDirectory: batchBuildOptions.DebugCaptureDirectory,
            LiveStdoutSink: _baseOptions.LiveStdoutSink,
            LiveStderrSink: _baseOptions.LiveStderrSink,
            ProgressDigestSink: _baseOptions.ProgressDigestSink,
            Size: batchTickets.Max(ticket =>
                WorkerSizeMapper.FromTicketSize(ticket.Size)),
            DebugTranscript: new DebugTranscriptContext(
                BuildVersion: _baseOptions.BuildVersion,
                SessionId: reworkSessionId,
                ReworkRound: feedback.ReworkRoundNumber));
        if (batchBuildOptions.DebugCaptureDirectory is not null)
            Directory.CreateDirectory(batchBuildOptions.DebugCaptureDirectory);

        var workerResult = await _batchWorker.ExecuteAsync(
            batchBrief, sharedWorktreePath, workerOptions, ct).ConfigureAwait(false);
        var reportedTickets = workerResult.Tickets;
        if (reportedTickets is null || reportedTickets.Count == 0)
            return null;

        var verifyBase = string.IsNullOrEmpty(mainSha) ? baseRef : mainSha;
        var verifyResult = await BatchCommitVerifier.VerifyAsync(
            _git,
            sharedWorktreePath,
            verifyBase,
            reportedTickets,
            ct).ConfigureAwait(false);
        if (!verifyResult.Success)
            return null;

        foreach (var confirmed in verifyResult.ConfirmedTickets)
        {
            var markerHtml =
                $"<p>[implemented_at: {confirmed.CommitSha}] " +
                $"(branch {batchBranchName}) " +
                $"(batch-rework: stack_position={confirmed.StackPosition})</p>";
            await BatchTicketWriter.RunBatchStateWriteAsync(
                confirmed.TicketId,
                () => _ticketing.CreateCommentAsync(
                    confirmed.TicketId, markerHtml, ct)).ConfigureAwait(false);
            await BatchTicketWriter.RunBatchStateWriteAsync(
                confirmed.TicketId,
                () => _ticketing.TransitionAsync(
                    confirmed.TicketId, TicketState.InReview, ct)).ConfigureAwait(false);
        }

        return verifyResult.ConfirmedTickets;
    }

    private async Task<ChainCommitRange?> ComputeBatchCommitRangeAsync(
        string? chainStartSha,
        CancellationToken ct)
    {
        if (chainStartSha is null)
            return null;
        try
        {
            var (_, currentTargetSha) = await BaseRefResolver.ResolveAsync(
                _git, _workingDirectory, _baseOptions.TargetBranch, ct)
                .ConfigureAwait(false);
            return await ChainCommitRangeHelper.ComputeAsync(
                _git,
                chainStartSha,
                currentTargetSha,
                _workingDirectory,
                ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> ResolveMainShaAsync(
        string baseRef,
        CancellationToken ct)
    {
        try
        {
            return await _git.RevParseAsync(
                baseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed record BatchReviewOutcome(
    bool Passed,
    VerdictKind FinalVerdict,
    string Rationale,
    IReadOnlyList<string> ChecksFailed);
