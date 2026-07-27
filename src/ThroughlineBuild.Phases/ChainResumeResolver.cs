using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public enum StartPhase
{
    Plan,
    Implement,
    ResumeImplement,
    Review,
    Refused
}

public sealed record ChainEntry(
    StartPhase StartPhase,
    ReviewFeedback? ResumeFeedback,
    int ResumeStartRound);

/// <summary>
/// Resolves the phase where a leaf chain enters and reconciles interrupted states.
/// </summary>
public sealed class ChainResumeResolver
{
    private const string SynthesizedResumeRationale =
        "Resume interrupted implementation: a prior implement round for this ticket did not finish. " +
        "Continue or redo the implementation from the current worktree state.";

    private readonly ITicketing _ticketing;
    private readonly IGitClient _git;
    private readonly IReviewFeedbackRetriever? _feedbackRetriever;
    private readonly ChainEventEmitter _events;

    public ChainResumeResolver(
        ITicketing ticketing,
        IGitClient git,
        IReviewFeedbackRetriever? feedbackRetriever,
        ChainEventEmitter events)
    {
        _ticketing = ticketing;
        _git = git;
        _feedbackRetriever = feedbackRetriever;
        _events = events;
    }

    /// <summary>
    /// Decides where the chain enters and performs state reconciliation for interrupted work.
    /// </summary>
    public async Task<ChainEntry> ResolveAsync(
        Ticket ticket,
        string workingDirectory,
        string targetBranch,
        CancellationToken ct)
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
                await _ticketing
                    .TransitionAsync(ticket.Id, TicketState.Backlog, ct)
                    .ConfigureAwait(false);
                await EmitResumeTransitionAsync(
                    ticket.Id,
                    "Planning",
                    "Backlog",
                    ct).ConfigureAwait(false);
                return new ChainEntry(StartPhase.Plan, null, 0);
            case TicketState.InProgress:
                return await ResolveInProgressAsync(
                    ticket,
                    workingDirectory,
                    targetBranch,
                    ct).ConfigureAwait(false);
            default:
                return new ChainEntry(StartPhase.Refused, null, 0);
        }
    }

    private async Task<ChainEntry> ResolveInProgressAsync(
        Ticket ticket,
        string workingDirectory,
        string targetBranch,
        CancellationToken ct)
    {
        var names = PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, workingDirectory);

        int commitsOnBranch = 0;
        try
        {
            var (baseRef, _) = await BaseRefResolver
                .ResolveAsync(_git, workingDirectory, targetBranch, ct)
                .ConfigureAwait(false);
            commitsOnBranch = await _git
                .RevListCountAsync($"{baseRef}..{names.BranchName}", workingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // A git failure, including an absent branch, is treated as no committed work.
        }

        if (commitsOnBranch == 0)
        {
            await PruneOrphanBranchAsync(
                names.BranchName,
                workingDirectory,
                ct).ConfigureAwait(false);
            await _ticketing
                .TransitionAsync(ticket.Id, TicketState.Ready, ct)
                .ConfigureAwait(false);
            await EmitResumeTransitionAsync(
                ticket.Id,
                "InProgress",
                "Ready",
                ct).ConfigureAwait(false);
            return new ChainEntry(StartPhase.Implement, null, 0);
        }

        var recovered = _feedbackRetriever?.GetLatestRework(ticket.Id);
        var feedback = recovered is not null
            ? recovered with { ReworkRoundNumber = 1 }
            : new ReviewFeedback(
                SynthesizedResumeRationale,
                Array.Empty<string>(),
                1);
        return new ChainEntry(StartPhase.ResumeImplement, feedback, 1);
    }

    private async Task PruneOrphanBranchAsync(
        string branchName,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
            foreach (var worktree in worktrees)
            {
                if (string.Equals(
                        worktree.Branch,
                        branchName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _git
                        .RemoveWorktreeAsync(worktree.Path, force: true, ct)
                        .ConfigureAwait(false);
                    break;
                }
            }
            await _git
                .DeleteBranchAsync(branchName, force: true, workingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Pruning is best-effort; the implement phase surfaces any retained collision.
        }
    }

    private Task EmitResumeTransitionAsync(
        string ticketId,
        string from,
        string to,
        CancellationToken ct) =>
        _events.EmitAsync(
            EventKind.StateTransition,
            ticketId,
            Phase.Chain,
            new Dictionary<string, object>
            {
                ["from"] = from,
                ["to"] = to,
                ["reason"] = "chain_resume"
            },
            ct);
}
