using System.Diagnostics;
using System.Net;
using System.Text;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Runs the planning and warm worker session for a chain batch implement group.
/// </summary>
public sealed class BatchImplementRunner
{
    private readonly IWorkerAgent? _batchWorker;
    private readonly ITicketing _ticketing;
    private readonly IGitClient _git;
    private readonly BuildOptions _baseOptions;
    private readonly string _workingDirectory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly Func<string, ChainEventEmitter> _eventEmitterFactory;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
    private readonly PhaseOptionsBuilder _phaseOptionsBuilder;

    public BatchImplementRunner(
        IWorkerAgent? batchWorker,
        ITicketing ticketing,
        IGitClient git,
        BuildOptions baseOptions,
        string workingDirectory,
        Func<string> sessionIdGenerator,
        Func<string, ChainEventEmitter> eventEmitterFactory,
        Func<BuildOptions, PlanPhase> planFactory,
        PhaseOptionsBuilder phaseOptionsBuilder)
    {
        _batchWorker = batchWorker;
        _ticketing = ticketing;
        _git = git;
        _baseOptions = baseOptions;
        _workingDirectory = workingDirectory;
        _sessionIdGenerator = sessionIdGenerator;
        _eventEmitterFactory = eventEmitterFactory;
        _planFactory = planFactory;
        _phaseOptionsBuilder = phaseOptionsBuilder;
    }

    internal IWorkerAgent? BatchWorker => _batchWorker;

    /// <summary>
    /// Checks a declared batch group against ticket-count, aggregate-size, and description-byte
    /// caps in that order. A non-null result tells the conductor to use its per-ticket fallback.
    /// </summary>
    internal static string? CheckBatchSizeCaps(
        IReadOnlyList<Ticket> batchTickets,
        BuildOptions options)
    {
        if (batchTickets.Count > options.BatchMaxTickets)
            return $"max_tickets={options.BatchMaxTickets} (actual {batchTickets.Count})";

        var sizeScore = batchTickets.Sum(ticket => ticket.Size switch
        {
            Size.S => 1,
            Size.M => 2,
            Size.L => 4,
            _ => 1
        });
        if (sizeScore > options.BatchMaxSizeScore)
            return $"max_size_score={options.BatchMaxSizeScore} (actual {sizeScore})";

        var descriptionBytes = batchTickets.Sum(ticket =>
            Encoding.UTF8.GetByteCount(ticket.DescriptionHtml ?? string.Empty));
        if (descriptionBytes > options.BatchMaxDescriptionBytes)
        {
            return $"max_description_bytes={options.BatchMaxDescriptionBytes} " +
                $"(actual {descriptionBytes})";
        }

        return null;
    }

    /// <summary>
    /// Plans one Backlog candidate before the batch worker starts.
    /// </summary>
    internal async Task<string?> PlanForBatchAsync(
        ChainPhaseOptions options,
        string ticketId,
        CancellationToken ct)
    {
        var sessionId = _sessionIdGenerator();
        var buildOptions = _phaseOptionsBuilder.BuildPhaseOptions(
            sessionId, ticketId, "plan", null, options.ChainTargetBranch);
        var childOptions = options with { TicketId = ticketId };
        _eventEmitterFactory(sessionId).EmitPhaseStart(
            childOptions, "plan", -1, sessionId);

        var stopwatch = Stopwatch.StartNew();
        var planResult = await _planFactory(buildOptions)
            .RunAsync(ticketId, _workingDirectory, ct).ConfigureAwait(false);
        stopwatch.Stop();

        childOptions.OnStep?.Invoke(ticketId, new ChainStep(
            PhaseName: "plan",
            ReworkRoundNumber: -1,
            Status: planResult.Success ? Status.Ok : Status.Failed,
            FailureReason: planResult.FailureReason,
            Verdict: null,
            Duration: stopwatch.Elapsed,
            PhaseSessionId: sessionId));
        return planResult.Success ? null : (planResult.FailureReason ?? "planning failed");
    }

    /// <summary>
    /// Runs one warm implement session for all tickets and converts ticketing outages into one
    /// TicketingUnavailable result plus resumable Skipped results for the remaining tickets.
    /// </summary>
    internal async Task<BatchImplementOutcome> RunBatchImplementSessionAsync(
        ChainPhaseOptions options,
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        string? chainStartSha,
        CancellationToken ct)
    {
        var activeTicketId = batchTickets[0].Id;
        var classifyStopwatch = Stopwatch.StartNew();
        try
        {
            return await RunBatchImplementSessionCoreAsync(
                options, batchTickets, sharedWorktreePath, baseRef, chainStartSha,
                id => activeTicketId = id, ct).ConfigureAwait(false);
        }
        catch (BatchTicketingUnavailableException ex)
        {
            classifyStopwatch.Stop();
            activeTicketId = ex.TicketId;
            var results = new List<ChainResult>(batchTickets.Count)
            {
                new(activeTicketId, Array.Empty<ChainStep>(), ChainOutcome.TicketingUnavailable,
                    classifyStopwatch.Elapsed, ex.TicketingException.Message)
            };
            results.AddRange(batchTickets
                .Where(ticket => !string.Equals(
                    ticket.Id, activeTicketId, StringComparison.Ordinal))
                .Select(ticket => new ChainResult(
                    ticket.Id, Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                    TimeSpan.Zero, null,
                    SkipReason:
                        $"ticketing backend unreachable while updating {activeTicketId}; " +
                        "restore connectivity and re-run")));
            return new BatchImplementOutcome(
                results.AsReadOnly(),
                null,
                PhaseWorktreeLayout.BranchName(batchTickets[0].Id),
                baseRef);
        }
    }

    private async Task<BatchImplementOutcome> RunBatchImplementSessionCoreAsync(
        ChainPhaseOptions options,
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        string? chainStartSha,
        Action<string> setActiveTicket,
        CancellationToken ct)
    {
        var batchStopwatch = Stopwatch.StartNew();
        var preparation = await PrepareSessionAsync(
            options, batchTickets, sharedWorktreePath, baseRef, chainStartSha,
            setActiveTicket, batchStopwatch, ct).ConfigureAwait(false);
        if (preparation.Failure is not null)
            return preparation.Failure;

        var session = preparation.Session!;
        if (session.WorkerResult.Status is Status.Failed or Status.Escalate)
        {
            return await HandleWorkerFailureAsync(
                batchTickets, sharedWorktreePath, baseRef, setActiveTicket,
                batchStopwatch, session, ct).ConfigureAwait(false);
        }

        return await HandleWorkerSuccessAsync(
            batchTickets, sharedWorktreePath, baseRef, setActiveTicket,
            batchStopwatch, session, ct).ConfigureAwait(false);
    }

    private async Task<SessionPreparation> PrepareSessionAsync(
        ChainPhaseOptions options,
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        string? chainStartSha,
        Action<string> setActiveTicket,
        Stopwatch batchStopwatch,
        CancellationToken ct)
    {
        var firstTicket = batchTickets[0];
        var batchBranchName = PhaseWorktreeLayout.BranchName(firstTicket.Id);
        var branchResult = await _git.CreateBranchAsync(
            batchBranchName, baseRef, sharedWorktreePath, ct).ConfigureAwait(false);
        if (!branchResult.Success)
        {
            batchStopwatch.Stop();
            var branchFailure = new ChainResult(
                firstTicket.Id,
                Array.Empty<ChainStep>(),
                ChainOutcome.StoppedAtImplement,
                batchStopwatch.Elapsed,
                $"batch implement: branch create for {batchBranchName} failed: " +
                branchResult.FailureReason);
            return new SessionPreparation(
                null,
                new BatchImplementOutcome(
                    new[] { branchFailure }, null, batchBranchName, baseRef));
        }

        foreach (var ticket in batchTickets)
        {
            setActiveTicket(ticket.Id);
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
            Directory.EnumerateFileSystemEntries(_workingDirectory).ToList().AsReadOnly());
        var batchSessionId = _sessionIdGenerator();
        var batchBrief = BatchImplementBriefBuilder.Build(
            _batchWorker!.Name,
            batchTickets,
            repoState,
            batchBranchName,
            sharedWorktreePath,
            batchCommitRange);

        _eventEmitterFactory(batchSessionId).EmitPhaseStart(
            options with { TicketId = firstTicket.Id },
            "batch-implement",
            -1,
            batchSessionId);

        var batchBuildOptions = _phaseOptionsBuilder.BuildPhaseOptions(
            batchSessionId, firstTicket.Id, "batch-implement", null, null);
        var workerOptions = BuildWorkerOptions(
            batchTickets, batchSessionId, batchBuildOptions.DebugCaptureDirectory);
        if (batchBuildOptions.DebugCaptureDirectory is not null)
            Directory.CreateDirectory(batchBuildOptions.DebugCaptureDirectory);

        var implementStopwatch = Stopwatch.StartNew();
        var workerResult = await _batchWorker
            .ExecuteAsync(batchBrief, sharedWorktreePath, workerOptions, ct)
            .ConfigureAwait(false);
        implementStopwatch.Stop();

        return new SessionPreparation(
            new BatchSession(
                batchBranchName,
                batchSessionId,
                mainSha,
                implementStopwatch.Elapsed,
                workerResult),
            null);
    }

    private async Task<BatchImplementOutcome> HandleWorkerFailureAsync(
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        Action<string> setActiveTicket,
        Stopwatch batchStopwatch,
        BatchSession session,
        CancellationToken ct)
    {
        var workerResult = session.WorkerResult;
        var failureReason = workerResult.FailureReason ?? workerResult.Summary;
        if (workerResult.Status == Status.Failed
            && workerResult.Tickets is { Count: > 0 })
        {
            var partialBase = EffectiveBase(session.MainSha, baseRef);
            var partialVerify = await BatchCommitVerifier.VerifyAsync(
                _git, sharedWorktreePath, partialBase, workerResult.Tickets, ct)
                .ConfigureAwait(false);
            if (partialVerify.Success)
            {
                await AdvanceConfirmedPartialTicketsAsync(
                    batchTickets, partialVerify.ConfirmedTickets, workerResult,
                    session.BatchBranchName, setActiveTicket, ct).ConfigureAwait(false);
                await PostPartialFailureCommentAsync(
                    batchTickets, partialVerify.ConfirmedTickets, failureReason,
                    session.BatchSessionId, ct).ConfigureAwait(false);

                batchStopwatch.Stop();
                return new BatchImplementOutcome(
                    BuildPartialResults(
                        batchTickets, partialVerify.ConfirmedTickets, failureReason,
                        session, batchStopwatch.Elapsed),
                    null,
                    session.BatchBranchName,
                    partialBase);
            }
        }

        batchStopwatch.Stop();
        return new BatchImplementOutcome(
            BuildStoppedResults(batchTickets, failureReason, batchStopwatch.Elapsed),
            null,
            session.BatchBranchName,
            baseRef);
    }

    private async Task<BatchImplementOutcome> HandleWorkerSuccessAsync(
        IReadOnlyList<Ticket> batchTickets,
        string sharedWorktreePath,
        string baseRef,
        Action<string> setActiveTicket,
        Stopwatch batchStopwatch,
        BatchSession session,
        CancellationToken ct)
    {
        var verifyBase = EffectiveBase(session.MainSha, baseRef);
        var reportedTickets = session.WorkerResult.Tickets;
        if (reportedTickets is null || reportedTickets.Count == 0)
        {
            var reconstruction = await BatchCommitVerifier.ReconstructFromGitAsync(
                _git,
                sharedWorktreePath,
                verifyBase,
                batchTickets.Select(ticket => ticket.Id).ToList(),
                ct).ConfigureAwait(false);
            if (!reconstruction.Success)
            {
                batchStopwatch.Stop();
                return new BatchImplementOutcome(
                    BuildStoppedResults(
                        batchTickets, reconstruction.FailureReason, batchStopwatch.Elapsed),
                    null,
                    session.BatchBranchName,
                    verifyBase);
            }
            reportedTickets = reconstruction.ConfirmedTickets;
        }

        var verification = await BatchCommitVerifier.VerifyAsync(
            _git, sharedWorktreePath, verifyBase, reportedTickets, ct)
            .ConfigureAwait(false);
        if (!verification.Success)
        {
            batchStopwatch.Stop();
            return new BatchImplementOutcome(
                BuildStoppedResults(
                    batchTickets, verification.FailureReason, batchStopwatch.Elapsed),
                null,
                session.BatchBranchName,
                verifyBase);
        }

        var results = await AdvanceSuccessfulTicketsAsync(
            batchTickets,
            verification.ConfirmedTickets,
            session,
            setActiveTicket,
            batchStopwatch,
            ct).ConfigureAwait(false);
        return new BatchImplementOutcome(
            results,
            verification.ConfirmedTickets,
            session.BatchBranchName,
            verifyBase);
    }

    private async Task AdvanceConfirmedPartialTicketsAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        WorkerResult workerResult,
        string batchBranchName,
        Action<string> setActiveTicket,
        CancellationToken ct)
    {
        foreach (var confirmedTicket in confirmedTickets)
        {
            var batchTicket = batchTickets.FirstOrDefault(ticket =>
                string.Equals(
                    ticket.Id, confirmedTicket.TicketId, StringComparison.Ordinal));
            if (batchTicket is null)
                continue;

            setActiveTicket(batchTicket.Id);
            var markerHtml = BuildImplementedMarker(
                confirmedTicket, batchBranchName, workerResult.Blocks);
            await BatchTicketWriter.RunBatchStateWriteAsync(
                batchTicket.Id,
                () => _ticketing.CreateCommentAsync(
                    batchTicket.Id, markerHtml, ct)).ConfigureAwait(false);
            await BatchTicketWriter.RunBatchStateWriteAsync(
                batchTicket.Id,
                () => _ticketing.TransitionAsync(
                    batchTicket.Id, TicketState.InReview, ct)).ConfigureAwait(false);
        }
    }

    private async Task PostPartialFailureCommentAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string failureReason,
        string batchSessionId,
        CancellationToken ct)
    {
        var confirmedIds = new HashSet<string>(
            confirmedTickets.Select(result => result.TicketId),
            StringComparer.Ordinal);
        var firstIncomplete = batchTickets.FirstOrDefault(
            ticket => !confirmedIds.Contains(ticket.Id));
        if (firstIncomplete is null)
            return;

        var failureHtml =
            $"<p>batch implement stopped: {WebUtility.HtmlEncode(failureReason)}</p>";
        await _eventEmitterFactory(batchSessionId).BestEffortTicketWriteAsync(
            firstIncomplete.Id,
            "batch_stopped_comment",
            ticketing => ticketing.CreateCommentAsync(
                firstIncomplete.Id, failureHtml, ct),
            ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChainResult>> AdvanceSuccessfulTicketsAsync(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        BatchSession session,
        Action<string> setActiveTicket,
        Stopwatch batchStopwatch,
        CancellationToken ct)
    {
        var results = new List<ChainResult>(batchTickets.Count);
        foreach (var ticket in batchTickets)
        {
            setActiveTicket(ticket.Id);
            var confirmed = confirmedTickets.FirstOrDefault(result =>
                string.Equals(result.TicketId, ticket.Id, StringComparison.Ordinal));
            if (confirmed is null)
            {
                results.Add(new ChainResult(
                    ticket.Id,
                    Array.Empty<ChainStep>(),
                    ChainOutcome.StoppedAtImplement,
                    batchStopwatch.Elapsed,
                    $"batch implement: no commit attribution for {ticket.Id}; the worker " +
                    "self-report omitted it and it could not be reconstructed from git"));
                continue;
            }

            var markerHtml = BuildImplementedMarker(
                confirmed, session.BatchBranchName, session.WorkerResult.Blocks);
            await BatchTicketWriter.RunBatchStateWriteAsync(
                ticket.Id,
                () => _ticketing.CreateCommentAsync(
                    ticket.Id, markerHtml, ct)).ConfigureAwait(false);
            await BatchTicketWriter.RunBatchStateWriteAsync(
                ticket.Id,
                () => _ticketing.TransitionAsync(
                    ticket.Id, TicketState.InReview, ct)).ConfigureAwait(false);

            results.Add(new ChainResult(
                ticket.Id,
                new[]
                {
                    new ChainStep(
                        "batch-implement",
                        0,
                        Status.Ok,
                        null,
                        null,
                        session.ImplementElapsed,
                        session.BatchSessionId)
                },
                ChainOutcome.BatchImplemented,
                batchStopwatch.Elapsed,
                $"batch implement succeeded; commit {confirmed.CommitSha}"));
        }
        return results.AsReadOnly();
    }

    private WorkerOptions BuildWorkerOptions(
        IReadOnlyList<Ticket> batchTickets,
        string batchSessionId,
        string? debugCaptureDirectory) =>
        new(
            _baseOptions.WorkerTimeout,
            _baseOptions.WorkerAllowedTools,
            DebugCaptureDirectory: debugCaptureDirectory,
            LiveStdoutSink: _baseOptions.LiveStdoutSink,
            LiveStderrSink: _baseOptions.LiveStderrSink,
            ProgressDigestSink: _baseOptions.ProgressDigestSink,
            Size: batchTickets.Max(ticket =>
                WorkerSizeMapper.FromTicketSize(ticket.Size)),
            DebugTranscript: new DebugTranscriptContext(
                BuildVersion: _baseOptions.BuildVersion,
                SessionId: batchSessionId));

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
                _git, chainStartSha, currentTargetSha, _workingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> ResolveMainShaAsync(string baseRef, CancellationToken ct)
    {
        try
        {
            return await _git.RevParseAsync(baseRef, _workingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildImplementedMarker(
        BatchTicketResult confirmedTicket,
        string batchBranchName,
        IReadOnlyDictionary<string, string>? blocks)
    {
        var summaryHtml = string.Empty;
        if (blocks is not null
            && !string.IsNullOrEmpty(confirmedTicket.SummaryRef)
            && blocks.TryGetValue(confirmedTicket.SummaryRef, out var summaryMarkdown)
            && !string.IsNullOrEmpty(summaryMarkdown))
        {
            summaryHtml = MarkdownRenderer.Render(summaryMarkdown);
        }

        return $"<p>[implemented_at: {confirmedTicket.CommitSha}] " +
            $"(branch {batchBranchName}) " +
            $"(batch: stack_position={confirmedTicket.StackPosition})</p>{summaryHtml}";
    }

    private static IReadOnlyList<ChainResult> BuildPartialResults(
        IReadOnlyList<Ticket> batchTickets,
        IReadOnlyList<BatchTicketResult> confirmedTickets,
        string failureReason,
        BatchSession session,
        TimeSpan elapsed)
    {
        var results = new List<ChainResult>(batchTickets.Count);
        foreach (var ticket in batchTickets)
        {
            var confirmed = confirmedTickets.FirstOrDefault(result =>
                string.Equals(result.TicketId, ticket.Id, StringComparison.Ordinal));
            results.Add(confirmed is null
                ? new ChainResult(
                    ticket.Id,
                    Array.Empty<ChainStep>(),
                    ChainOutcome.StoppedAtImplement,
                    elapsed,
                    failureReason)
                : new ChainResult(
                    ticket.Id,
                    new[]
                    {
                        new ChainStep(
                            "batch-implement",
                            0,
                            Status.Ok,
                            null,
                            null,
                            session.ImplementElapsed,
                            session.BatchSessionId)
                    },
                    ChainOutcome.BatchImplemented,
                    elapsed,
                    $"batch implement succeeded; commit {confirmed.CommitSha}"));
        }
        return results.AsReadOnly();
    }

    private static IReadOnlyList<ChainResult> BuildStoppedResults(
        IReadOnlyList<Ticket> batchTickets,
        string? failureReason,
        TimeSpan elapsed) =>
        batchTickets.Select(ticket => new ChainResult(
            ticket.Id,
            Array.Empty<ChainStep>(),
            ChainOutcome.StoppedAtImplement,
            elapsed,
            failureReason)).ToList().AsReadOnly();

    private static string EffectiveBase(string mainSha, string baseRef) =>
        string.IsNullOrEmpty(mainSha) ? baseRef : mainSha;

    private sealed record SessionPreparation(
        BatchSession? Session,
        BatchImplementOutcome? Failure);

    private sealed record BatchSession(
        string BatchBranchName,
        string BatchSessionId,
        string MainSha,
        TimeSpan ImplementElapsed,
        WorkerResult WorkerResult);
}

internal sealed record BatchImplementOutcome(
    IReadOnlyList<ChainResult> Results,
    IReadOnlyList<BatchTicketResult>? ConfirmedTickets,
    string BranchName,
    string BaseRef);
