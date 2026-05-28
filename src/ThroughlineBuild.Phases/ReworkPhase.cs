using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;

namespace ThroughlineBuild.Phases;

public enum ReworkOutcome { Implemented, NoFeedbackAvailable, TicketNotInProgress, ImplementFailed }

public sealed record ReworkResult(
    string TicketId,
    ReworkOutcome Outcome,
    ImplementResult? ImplementResult,
    string? FailureReason,
    string FeedbackSource);

public sealed record ReworkPhaseOptions(
    string TicketId,
    string? ManualFeedback,
    int ReworkRoundNumber,
    bool Debug);

public interface IReviewFeedbackRetriever
{
    ReviewFeedback? GetLatestRework(string ticketId);
}

public sealed class ReworkPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IReviewFeedbackRetriever _retriever;
    private readonly ReworkPhaseOptions _phaseOptions;
    private readonly IGitClient? _git;
    private readonly ProjectContext? _project;

    public ReworkPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IReviewFeedbackRetriever retriever,
        ReworkPhaseOptions phaseOptions,
        IGitClient? gitClient = null,
        ProjectContext? project = null)
    {
        _ticketing = ticketing;
        _worker = worker;
        _events = events;
        _options = options;
        _retriever = retriever;
        _phaseOptions = phaseOptions;
        _git = gitClient;
        _project = project;
    }

    public Phase Phase => Phase.Implement;

    public async Task<ReworkResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Read ticket state
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Step 2: State precondition
        if (ticket.State != TicketState.InProgress)
        {
            return new ReworkResult(
                ticketId,
                ReworkOutcome.TicketNotInProgress,
                null,
                $"ticket is in {ticket.State}; if Rework was the verdict, this is unexpected - state should have transitioned to InProgress",
                "");
        }

        // Step 3: Resolve feedback source
        ReviewFeedback feedback;
        string feedbackSource;

        if (_phaseOptions.ManualFeedback is not null)
        {
            feedback = new ReviewFeedback(_phaseOptions.ManualFeedback, Array.Empty<string>(), _phaseOptions.ReworkRoundNumber);
            feedbackSource = "manual";
        }
        else
        {
            var retrieved = _retriever.GetLatestRework(ticketId);
            if (retrieved is null)
            {
                return new ReworkResult(
                    ticketId,
                    ReworkOutcome.NoFeedbackAvailable,
                    null,
                    $"no Rework verdict found in event log for ticket {ticketId}; if the review was on a different machine, supply --feedback",
                    "");
            }
            // Step 5: Override ReworkRoundNumber with caller-provided value
            feedback = retrieved with { ReworkRoundNumber = _phaseOptions.ReworkRoundNumber };
            feedbackSource = "event-log";
        }

        // Step 6: Build ImplementPhaseOptions
        var implementOptions = new ImplementPhaseOptions(feedback);

        // Step 7: Invoke ImplementPhase
        var inner = new ImplementPhase(_ticketing, _worker, _events, _options, _git, _project, implementOptions);
        var implementResult = await inner.RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);

        // Step 8: Return result
        if (implementResult.Success)
            return new ReworkResult(ticketId, ReworkOutcome.Implemented, implementResult, null, feedbackSource);
        else
            return new ReworkResult(ticketId, ReworkOutcome.ImplementFailed, implementResult, implementResult.FailureReason, feedbackSource);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var reworkResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        bool success = reworkResult.Outcome == ReworkOutcome.Implemented;

        var outputs = new Dictionary<string, string>
        {
            ["outcome"] = reworkResult.Outcome.ToString(),
            ["feedback_source"] = reworkResult.FeedbackSource
        };

        if (reworkResult.ImplementResult is { Success: true } ir)
        {
            if (ir.CommitSha is not null) outputs["commit_sha"] = ir.CommitSha;
            if (ir.BranchName is not null) outputs["branch"] = ir.BranchName;
            if (ir.WorktreePath is not null) outputs["worktree_path"] = ir.WorktreePath;
        }

        return new PhaseResult(success, reworkResult.TicketId, Phase.Implement, reworkResult.FailureReason, outputs);
    }
}
