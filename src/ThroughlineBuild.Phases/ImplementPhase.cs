using System.Text.Json;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public record ImplementResult(
    bool Success,
    string TicketId,
    string? CommitSha,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason);

public class ImplementPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IWorkerAgent _worker;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly IGitClient _git;

    public ImplementPhase(
        ITicketing ticketing,
        IWorkerAgent worker,
        IEventSink events,
        BuildOptions options,
        IGitClient? gitClient = null)
    {
        _ticketing = ticketing;
        _worker = worker;
        _events = events;
        _options = options;
        _git = gitClient ?? new ProcessGitClient();
    }

    public Phase Phase => Phase.Implement;

    public async Task<ImplementResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Fetch ticket
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Step 2: Validate state
        if (ticket.State != TicketState.Ready)
            return new ImplementResult(false, ticketId, null, null, null, "ticket not in Ready state");

        // Step 3: Get current main SHA
        string mainSha;
        try
        {
            mainSha = await _git.RevParseAsync("origin/main", workingDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ImplementResult(false, ticketId, null, null, null, $"git rev-parse failed: {ex.Message}");
        }

        // Step 4: Compute worktree names
        var worktreeNames = PhaseWorktreeLayout.Compute(ticketId, ticket.Title, workingDirectory);

        // Step 5: Drift check - scan comments for [planned_at: <sha>]
        var comments = await _ticketing.GetCommentsAsync(ticketId, ct).ConfigureAwait(false);
        string? plannedAtSha = null;
        foreach (var comment in comments)
        {
            var markers = MarkerParser.Parse(comment.Body);
            foreach (var m in markers)
            {
                if (m.Name == "planned_at" && !string.IsNullOrEmpty(m.Value))
                {
                    plannedAtSha = m.Value;
                    break;
                }
            }
            if (plannedAtSha is not null) break;
        }
        if (plannedAtSha is not null && plannedAtSha != mainSha)
        {
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "drift_warning",
                ["planned_at_sha"] = plannedAtSha,
                ["main_sha"] = mainSha
            }, ct).ConfigureAwait(false);
        }

        // Step 6: Build RepoState
        var topLevelEntries = Directory.EnumerateFileSystemEntries(workingDirectory).ToList().AsReadOnly();
        var repoState = new RepoState(mainSha, topLevelEntries);

        // Step 7: Build brief
        var brief = ImplementBriefBuilder.Build(ticket, repoState, worktreeNames.BranchName, worktreeNames.WorktreePath);

        // Step 8: Create worktree
        var createResult = await _git.CreateWorktreeAsync(
            worktreeNames.WorktreePath,
            worktreeNames.BranchName,
            "origin/main",
            workingDirectory,
            ct).ConfigureAwait(false);
        if (!createResult.Success)
            return new ImplementResult(false, ticketId, null, worktreeNames.BranchName, worktreeNames.WorktreePath,
                $"worktree create failed: {createResult.FailureReason}");

        // Step 9: Transition Ready -> InProgress
        await _ticketing.TransitionAsync(ticketId, TicketState.InProgress, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = "Ready",
            ["to"] = "InProgress"
        }, ct).ConfigureAwait(false);

        // Step 10: Emit WorkerSpawn
        await EmitAsync(EventKind.WorkerSpawn, ticketId, new Dictionary<string, object>
        {
            ["worker"] = _options.WorkerName
        }, ct).ConfigureAwait(false);

        // Step 11: Execute worker inside the worktree
        var workerOptions = new WorkerOptions(_options.WorkerTimeout, _options.WorkerAllowedTools);
        var workerResult = await _worker.ExecuteAsync(brief, worktreeNames.WorktreePath, workerOptions, ct).ConfigureAwait(false);

        // Step 12: Emit VerifierVerdict
        await EmitAsync(EventKind.VerifierVerdict, ticketId, new Dictionary<string, object>
        {
            ["status"] = workerResult.Status.ToString()
        }, ct).ConfigureAwait(false);

        // Step 13: LlmCall event if usage present
        if (workerResult.Metadata.TryGetValue("llm_usage", out var usageObj))
        {
            var llmData = LlmUsageFlattener.Flatten(usageObj);
            if (llmData is not null)
            {
                await EmitAsync(EventKind.LlmCall, ticketId, llmData, ct).ConfigureAwait(false);
            }
        }

        // Step 14: If worker failed, leave in InProgress
        if (workerResult.Status != Status.Ok)
            return new ImplementResult(false, ticketId, null, worktreeNames.BranchName, worktreeNames.WorktreePath,
                workerResult.FailureReason ?? workerResult.Status.ToString());

        // Step 15: Extract commit_sha from metadata
        var metadataCommitSha = TryGetString(workerResult.Metadata, "commit_sha");
        if (string.IsNullOrEmpty(metadataCommitSha))
            return new ImplementResult(false, ticketId, null, worktreeNames.BranchName, worktreeNames.WorktreePath,
                "worker metadata missing commit_sha");

        // Step 16: Verify against actual HEAD; prefer actual HEAD if it differs
        var actualHeadSha = await _git.HeadShaAsync(worktreeNames.WorktreePath, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(actualHeadSha))
            actualHeadSha = metadataCommitSha;
        var discrepancyNote = (actualHeadSha != metadataCommitSha)
            ? $" (worker reported {metadataCommitSha}, HEAD is {actualHeadSha})"
            : "";

        // Step 17: Post implemented_at comment
        var commentHtml = $"<p>[implemented_at: {actualHeadSha}] (branch {worktreeNames.BranchName}){discrepancyNote}</p>";
        await _ticketing.CreateCommentAsync(ticketId, commentHtml, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        // Step 18: Transition InProgress -> InReview
        await _ticketing.TransitionAsync(ticketId, TicketState.InReview, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = "InProgress",
            ["to"] = "InReview"
        }, ct).ConfigureAwait(false);

        // Step 19: Return success
        return new ImplementResult(true, ticketId, actualHeadSha, worktreeNames.BranchName, worktreeNames.WorktreePath, null);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var implResult = await RunAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        var outputs = implResult.Success
            ? new Dictionary<string, string>
            {
                ["commit_sha"] = implResult.CommitSha!,
                ["branch"] = implResult.BranchName!,
                ["worktree_path"] = implResult.WorktreePath!
            } as IReadOnlyDictionary<string, string>
            : new Dictionary<string, string>() as IReadOnlyDictionary<string, string>;
        return new PhaseResult(implResult.Success, implResult.TicketId, Phase.Implement, implResult.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Implement,
            data), ct).ConfigureAwait(false);
    }


    private static string? TryGetString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }
}
