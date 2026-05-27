using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Phases;

public record ShipOptions(
    IReadOnlyList<CheckSpec> RegressionChecks,
    string Remote,
    string BaseBranch,
    bool DeleteFeatureBranch = true);

public record ShipResult(
    bool Success,
    string TicketId,
    string? MergedSha,
    string? FailureReason,
    ShipFailureStage? FailedAt);

public enum ShipFailureStage
{
    StateCheck,
    PreFlight,
    Fetch,
    Rebase,
    ConflictMarkerScan,
    RegressionChecks,
    FastForwardMerge,
    Decruft
}

public delegate Task<IReadOnlyList<ConflictMarkerHit>> ConflictMarkerScannerFn(
    IEnumerable<string> filePaths, CancellationToken ct);

public class ShipPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;
    private readonly ShipOptions _shipOptions;
    private readonly IGitClient _git;
    private readonly AutomatedChecksRunner _checksRunner;
    private readonly ConflictMarkerScannerFn _markerScanner;
    private readonly WorktreeDecrufter _decrufter;

    public ShipPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        ShipOptions shipOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null,
        ConflictMarkerScannerFn? markerScanner = null,
        WorktreeDecrufter? decrufter = null)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
        _shipOptions = shipOptions;
        _git = gitClient ?? new ProcessGitClient();
        _checksRunner = checksRunner ?? new AutomatedChecksRunner();
        _markerScanner = markerScanner ?? ConflictMarkerScanner.ScanAsync;
        _decrufter = decrufter ?? new WorktreeDecrufter(_git);
    }

    public Phase Phase => Phase.Ship;

    public async Task<ShipResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var (result, _, _) = await RunInternalAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(ShipResult Result, PhaseWorktreeNames? WorktreeNames, string? CanonicalPath)> RunInternalAsync(
        string ticketId, string workingDirectory, CancellationToken ct)
    {
        // Step 1: Fetch ticket
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Step 2: Validate state
        if (ticket.State != TicketState.InReview)
            return (new ShipResult(false, ticketId, null, "ticket not in InReview state", ShipFailureStage.StateCheck), null, null);

        // Step 3: Compute and locate worktree
        var worktreeNames = PhaseWorktreeLayout.Compute(ticketId, ticket.Title, workingDirectory);
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        bool worktreeFound = false;
        // Default to the computed path; overwritten below with the canonical path from git.
        string canonicalWorktreePath = worktreeNames.WorktreePath;
        foreach (var w in worktrees)
        {
            if (w.Branch == worktreeNames.BranchName)
            {
                canonicalWorktreePath = w.Path;
                worktreeFound = true;
                break;
            }
            string wPathFull;
            try { wPathFull = Path.GetFullPath(w.Path); }
            catch { wPathFull = w.Path; }
            if (string.Equals(wPathFull, worktreeNames.WorktreePath, StringComparison.OrdinalIgnoreCase))
            {
                canonicalWorktreePath = w.Path;
                worktreeFound = true;
                break;
            }
        }
        if (!worktreeFound)
            return (new ShipResult(false, ticketId, null,
                $"feature worktree not found at {worktreeNames.WorktreePath}",
                ShipFailureStage.StateCheck), worktreeNames, null);

        // Step 3b: Pre-flight dirty check - both feature and main worktrees must be clean
        var featureChanges = await _git.GetTrackedChangesAsync(canonicalWorktreePath, ct).ConfigureAwait(false);
        var mainChanges = await _git.GetTrackedChangesAsync(workingDirectory, ct).ConfigureAwait(false);
        if (featureChanges.Count > 0 || mainChanges.Count > 0)
        {
            var dirtyParts = new List<string>();
            if (featureChanges.Count > 0)
                dirtyParts.Add($"{canonicalWorktreePath} has {featureChanges.Count} modified tracked files - commit or stash before shipping");
            if (mainChanges.Count > 0)
                dirtyParts.Add($"{workingDirectory} has {mainChanges.Count} modified tracked files - commit or stash before shipping");
            var dirtyMessage = string.Join("; ", dirtyParts);
            var dirtyPaths = new List<string>();
            if (featureChanges.Count > 0) dirtyPaths.Add(canonicalWorktreePath);
            if (mainChanges.Count > 0) dirtyPaths.Add(workingDirectory);
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> uncommitted tracked changes in: {string.Join(", ", dirtyPaths)}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "pre_flight_dirty",
                ["dirty_paths"] = (IReadOnlyList<string>)dirtyPaths
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null, dirtyMessage, ShipFailureStage.PreFlight), worktreeNames, null);
        }

        // Step 4: Check for remote and conditionally fetch
        var remote = _shipOptions.Remote;
        var baseBranch = _shipOptions.BaseBranch;
        var remoteExists = await _git.RemoteExistsAsync(remote, workingDirectory, ct).ConfigureAwait(false);
        string ontoRef;
        if (!remoteExists)
        {
            // No remote configured - skip fetch and rebase onto local base branch
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "fetch_skipped",
                ["reason"] = "no_remote",
                ["remote"] = remote
            }, ct).ConfigureAwait(false);
            ontoRef = baseBranch;
        }
        else
        {
            var fetchResult = await _git.FetchAsync(remote, workingDirectory, ct).ConfigureAwait(false);
            if (!fetchResult.Success)
                return (new ShipResult(false, ticketId, null,
                    $"git fetch failed: {fetchResult.FailureReason}",
                    ShipFailureStage.Fetch), worktreeNames, null);
            ontoRef = $"{remote}/{baseBranch}";
        }

        // Step 5: Rebase feature branch onto ontoRef
        var rebaseResult = await _git.RebaseAsync(ontoRef, canonicalWorktreePath, ct).ConfigureAwait(false);
        if (rebaseResult.HadConflicts)
        {
            await _git.RebaseAbortAsync(canonicalWorktreePath, ct).ConfigureAwait(false);
            var paths = string.Join(", ", rebaseResult.ConflictingPaths);
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> rebase conflicts in: {paths}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "rebase_conflicts",
                ["conflicting_paths"] = rebaseResult.ConflictingPaths
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null,
                $"rebase conflicts in: {paths}", ShipFailureStage.Rebase), worktreeNames, null);
        }
        if (!rebaseResult.Success)
        {
            var reason = rebaseResult.FailureReason ?? "unknown";
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> rebase failed: {reason}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "rebase_other",
                ["reason"] = reason
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null,
                $"rebase failed: {reason}", ShipFailureStage.Rebase), worktreeNames, null);
        }

        // Step 6: Conflict-marker scan (post-rebase, pre-checks)
        var diff = await _git.DiffAsync(ontoRef, worktreeNames.BranchName, workingDirectory,
            includePatchContent: false, ct).ConfigureAwait(false);
        var scanPaths = diff.Entries
            .Select(e => Path.Combine(canonicalWorktreePath, e.Path))
            .ToList();
        var markerHits = await _markerScanner(scanPaths, ct).ConfigureAwait(false);
        if (markerHits.Count > 0)
        {
            var distinctMarkerFiles = markerHits.Select(h => h.Path).Distinct().ToList();
            var pathsList = string.Join(", ", distinctMarkerFiles);
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> conflict markers detected in: {pathsList}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "conflict_markers",
                ["marker_files"] = distinctMarkerFiles
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null,
                $"conflict markers detected in: {pathsList}", ShipFailureStage.ConflictMarkerScan), worktreeNames, null);
        }

        // Step 7: Regression checks
        var checkResults = await _checksRunner.RunAsync(_shipOptions.RegressionChecks, canonicalWorktreePath, ct).ConfigureAwait(false);
        var checksFailed = checkResults.Where(r => !r.Passed).Select(r => r.Name).ToList();
        if (checksFailed.Count > 0)
        {
            var namesList = string.Join(", ", checksFailed);
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> regression checks failed: {namesList}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "regression_checks",
                ["checks_failed"] = checksFailed
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null,
                $"regression checks failed: {namesList}", ShipFailureStage.RegressionChecks), worktreeNames, null);
        }

        // Step 8: Fast-forward merge into local baseBranch (main worktree)
        var ffResult = await _git.FastForwardMergeAsync(worktreeNames.BranchName, workingDirectory, ct).ConfigureAwait(false);
        if (!ffResult.Success)
            return (new ShipResult(false, ticketId, null,
                $"fast-forward merge failed: {ffResult.FailureReason}",
                ShipFailureStage.FastForwardMerge), worktreeNames, null);

        // Step 9: Read merged HEAD sha
        var mergedSha = await _git.HeadShaAsync(workingDirectory, ct).ConfigureAwait(false);

        // Step 10: Post shipped_at comment
        await _ticketing.CreateCommentAsync(ticketId,
            $"<p>[shipped_at: {mergedSha}]</p>", ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        // Step 11: Transition InReview -> Done
        await _ticketing.TransitionAsync(ticketId, TicketState.Done, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = "InReview",
            ["to"] = "Done"
        }, ct).ConfigureAwait(false);

        // Step 12: Decruft worktree. Failure must not unwind Done.
        string decruftHaltedAt;
        string? decruftError = null;
        try
        {
            var decruftResult = await _decrufter.DecruftAsync(canonicalWorktreePath, workingDirectory, ct).ConfigureAwait(false);
            decruftHaltedAt = decruftResult.HaltedAt?.ToString() ?? "complete";
        }
        catch (Exception ex)
        {
            decruftHaltedAt = "exception";
            decruftError = ex.Message;
        }
        var decruftData = new Dictionary<string, object>
        {
            ["action"] = "decruft",
            ["halted_at"] = decruftHaltedAt
        };
        if (decruftError is not null)
            decruftData["error"] = decruftError;
        await EmitAsync(EventKind.TicketWrite, ticketId, decruftData, ct).ConfigureAwait(false);

        // Step 13: Optionally delete feature branch. Failure does not unwind Done.
        if (_shipOptions.DeleteFeatureBranch)
        {
            var deleteResult = await _git.DeleteBranchAsync(worktreeNames.BranchName, force: false, workingDirectory, ct).ConfigureAwait(false);
            var deleteData = new Dictionary<string, object>
            {
                ["action"] = "delete_branch",
                ["success"] = deleteResult.Success
            };
            if (deleteResult.FailureReason is not null)
                deleteData["reason"] = deleteResult.FailureReason;
            await EmitAsync(EventKind.TicketWrite, ticketId, deleteData, ct).ConfigureAwait(false);
        }

        // Step 14: Return success
        return (new ShipResult(true, ticketId, mergedSha, null, null), worktreeNames, canonicalWorktreePath);
    }

    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        var (shipResult, worktreeNames, canonicalPath) = await RunInternalAsync(ticketId, workingDirectory, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> outputs;
        if (shipResult.Success && worktreeNames is not null && canonicalPath is not null)
        {
            outputs = new Dictionary<string, string>
            {
                ["merged_sha"] = shipResult.MergedSha ?? "",
                ["branch"] = worktreeNames.BranchName,
                ["worktree_path"] = canonicalPath
            };
        }
        else
        {
            outputs = new Dictionary<string, string>();
        }
        return new PhaseResult(shipResult.Success, shipResult.TicketId, Phase.Ship, shipResult.FailureReason, outputs);
    }

    private async Task EmitAsync(EventKind kind, string ticketId, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            ticketId,
            Phase.Ship,
            data), ct).ConfigureAwait(false);
    }
}
