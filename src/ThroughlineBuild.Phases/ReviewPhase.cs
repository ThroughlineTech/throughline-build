using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Verification;
using static ThroughlineBuild.Helpers.LlmUsageFlattener;

namespace ThroughlineBuild.Phases;

public record ReviewOptions(
    IReadOnlyList<CheckSpec> Checks,
    WorkerOptions VerifierWorkerOptions);

public record ReviewResult(
    bool Success,
    string TicketId,
    VerdictKind? Verdict,
    string? VerdictRationale,
    IReadOnlyList<string> ChecksFailed,
    string? FailureReason);

public class ReviewPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _verifierWorker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly ReviewOptions _reviewOptions;
    private readonly IGitClient _git;
    private readonly IVerifier? _verifierOverride;
    private readonly AutomatedChecksRunner? _checksRunner;

    public ReviewPhase(
        ITicketing ticketing,
        IWorkerAgent verifierWorker,
        IEventSink events,
        BuildOptions options,
        ReviewOptions reviewOptions,
        IGitClient? gitClient = null,
        IVerifier? verifierOverride = null,
        AutomatedChecksRunner? checksRunner = null)
    {
        _ticketing = ticketing;
        _verifierWorker = verifierWorker;
        _events = events;
        _options = options;
        _reviewOptions = reviewOptions;
        _git = gitClient ?? new ProcessGitClient();
        _verifierOverride = verifierOverride;
        _checksRunner = checksRunner;
    }

    public Phase Phase => Phase.Review;

    public async Task<ReviewResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Fetch ticket
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Step 2: Validate state
        if (ticket.State != TicketState.InReview)
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                "ticket not in InReview state");

        // Step 3: Compute and locate worktree
        var worktreeNames = PhaseWorktreeLayout.Compute(ticketId, ticket.Title, workingDirectory);
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        bool worktreeFound = false;
        foreach (var w in worktrees)
        {
            if (w.Branch == worktreeNames.BranchName)
            {
                worktreeFound = true;
                break;
            }
            string wPathFull;
            try { wPathFull = Path.GetFullPath(w.Path); }
            catch { wPathFull = w.Path; }
            if (string.Equals(wPathFull, worktreeNames.WorktreePath, StringComparison.OrdinalIgnoreCase))
            {
                worktreeFound = true;
                break;
            }
        }
        if (!worktreeFound)
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                $"feature worktree not found at {worktreeNames.WorktreePath}");

        // Step 4: Get current main SHA
        string mainSha;
        try
        {
            mainSha = await _git.RevParseAsync("origin/main", workingDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                $"git rev-parse failed: {ex.Message}");
        }

        // Step 5: Build RepoState and implementer brief
        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);
        var implementerBrief = ImplementBriefBuilder.Build(ticket, repoState, worktreeNames.BranchName, worktreeNames.WorktreePath);

        // Step 6a: Reconstruct implementer commit SHA from [implemented_at: <sha>] marker
        var comments = await _ticketing.GetCommentsAsync(ticketId, ct).ConfigureAwait(false);
        string? implementerCommitSha = null;
        foreach (var comment in comments)
        {
            var markers = MarkerParser.Parse(comment.Body);
            foreach (var m in markers)
            {
                if (m.Name == "implemented_at" && !string.IsNullOrEmpty(m.Value))
                {
                    implementerCommitSha = m.Value;
                }
            }
        }
        if (string.IsNullOrEmpty(implementerCommitSha))
            return new ReviewResult(false, ticketId, null, null, Array.Empty<string>(),
                "no implemented_at marker found - ticket reached InReview without an implement marker, ReviewPhase cannot reconstruct implementer state");

        // Step 6b: Compute diff and synthesize implementer WorkerResult
        var diff = await _git.DiffAsync("origin/main", worktreeNames.BranchName, workingDirectory, includePatchContent: true, ct).ConfigureAwait(false);
        var implementerResult = new WorkerResult(
            Status.Ok,
            $"Reconstructed from implemented_at: {implementerCommitSha} ({diff.Entries.Count} files changed)",
            diff.Entries.Select(e => e.Path).ToList(),
            null,
            new Dictionary<string, object>
            {
                ["commit_sha"] = implementerCommitSha
            });

        // Step 7: Run automated checks (failures do not short-circuit; verifier sees them)
        var runner = _checksRunner ?? new AutomatedChecksRunner();
        var checkResults = await runner.RunAsync(_reviewOptions.Checks, worktreeNames.WorktreePath, ct).ConfigureAwait(false);

        // Step 8: Construct verifier
        var verifier = _verifierOverride
            ?? new ClaudeCodeReviewer(_verifierWorker, ticket, checkResults, _reviewOptions.VerifierWorkerOptions, workingDirectory);

        // Step 9: Emit WorkerSpawn (role = verifier)
        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _verifierWorker.Name,
            ["role"] = "verifier"
        }, ct).ConfigureAwait(false);

        // Step 10: Run verifier
        var verdict = await verifier.VerifyAsync(implementerBrief, diff, implementerResult, ct).ConfigureAwait(false);

        // Step 11: Emit VerifierVerdict
        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["kind"] = verdict.Kind.ToString(),
            ["checks_failed_count"] = verdict.ChecksFailed.Count
        }, ct).ConfigureAwait(false);

        // Step 12: LlmCall event if verifier worker reported usage
        if (_verifierOverride is null && verifier is ClaudeCodeReviewer ccr && ccr.LastWorkerResult is { } verifierResult && verifierResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
            }
        }

        // Step 13: Apply verdict
        string commentHtml;
        if (verdict.Kind == VerdictKind.Pass)
        {
            commentHtml = $"<p><strong>reviewed:</strong> pass - {verdict.Rationale}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
        }
        else if (verdict.Kind == VerdictKind.Rework)
        {
            var checksList = verdict.ChecksFailed.Count > 0
                ? "<br/>checks_failed: " + string.Join(", ", verdict.ChecksFailed)
                : "";
            commentHtml = $"<p><strong>reviewed:</strong> rework - {verdict.Rationale}{checksList}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
            await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
            {
                ["from"] = "InReview",
                ["to"] = "InProgress"
            }, ct).ConfigureAwait(false);
        }
        else // Fail
        {
            var checksList = verdict.ChecksFailed.Count > 0
                ? "<br/>checks_failed: " + string.Join(", ", verdict.ChecksFailed)
                : "";
            commentHtml = $"<p><strong>reviewed:</strong> fail - {verdict.Rationale}{checksList}</p>";
            await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "create_comment"
            }, ct).ConfigureAwait(false);
        }

        // Step 14: Return success
        return new ReviewResult(true, ticketId, verdict.Kind, verdict.Rationale, verdict.ChecksFailed, null);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var reviewResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = reviewResult.Success
            ? new Dictionary<string, string>
            {
                ["verdict"] = reviewResult.Verdict?.ToString() ?? "",
                ["rationale"] = reviewResult.VerdictRationale ?? "",
                ["checks_failed_count"] = reviewResult.ChecksFailed.Count.ToString()
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(reviewResult.Success, reviewResult.TicketId, Phase.Review, reviewResult.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Review,
            data), ct).ConfigureAwait(false);
    }

}
