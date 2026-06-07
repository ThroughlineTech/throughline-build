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
    bool DeleteFeatureBranch = true,
    bool NoAutoMerge = false,
    string? TargetBranch = null,
    bool SkipDecruft = false,
    bool NoPush = false,
    bool TargetBranchOverridden = false,
    bool SkipBaseline = false,
    BaselineCache? BaselineCache = null);

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
    Push,
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
    private readonly Func<string?> _processPathProvider;
    private readonly TextWriter? _progress;
    private readonly bool _verbose;

    public ShipPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions options,
        ShipOptions shipOptions,
        IGitClient? gitClient = null,
        AutomatedChecksRunner? checksRunner = null,
        ConflictMarkerScannerFn? markerScanner = null,
        WorktreeDecrufter? decrufter = null,
        Func<string?>? processPathProvider = null,
        TextWriter? progressWriter = null,
        bool verbose = false)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
        _shipOptions = shipOptions;
        _git = gitClient ?? new ProcessGitClient();
        _checksRunner = checksRunner ?? new AutomatedChecksRunner();
        _markerScanner = markerScanner ?? ConflictMarkerScanner.ScanAsync;
        _decrufter = decrufter ?? new WorktreeDecrufter(_git);
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        _progress = progressWriter;
        _verbose = verbose;
    }

    private void ReportProgress(string message) => _progress?.WriteLine(message);

    private void ReportGitOutput(string label, string? rawOutput)
    {
        if (!_verbose || _progress == null || string.IsNullOrWhiteSpace(rawOutput)) return;
        _progress.WriteLine($"[ship] {label}:");
        _progress.WriteLine(rawOutput.TrimEnd());
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
        ReportProgress($"[ship] fetching ticket {ticketId}...");
        var ticket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);

        // Parent-ticket ship path: validate all children Done
        ReportProgress("[ship] checking for child tickets...");
        var shipChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (shipChildren.Count > 0)
        {
            return (await RunParentShipAsync(ticketId, ticket, shipChildren, ct).ConfigureAwait(false), null, null);
        }

        // Step 2: Validate state
        if (ticket.State != TicketState.InReview)
            return (new ShipResult(false, ticketId, null, "ticket not in InReview state", ShipFailureStage.StateCheck), null, null);

        // Step 3: Compute and locate worktree
        var worktreeNames = PhaseWorktreeLayout.Compute(ticket.Id, ticket.Title, workingDirectory);
        var worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
        bool worktreeFound = false;
        // Default to the computed path/branch; overwritten below with the canonical values from git.
        string canonicalWorktreePath = worktreeNames.WorktreePath;
        string canonicalBranchName = worktreeNames.BranchName;
        var ticketBranchName = worktreeNames.BranchName;
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
                canonicalBranchName = string.IsNullOrEmpty(w.Branch) ? worktreeNames.BranchName : w.Branch;
                worktreeFound = true;
                break;
            }
            if (PhaseWorktreeLayout.IsTicketBranch(w.Branch, ticketBranchName))
            {
                canonicalWorktreePath = w.Path;
                canonicalBranchName = w.Branch;
                worktreeFound = true;
                break;
            }
        }
        // Fallback: find the local ticket branch and create a worktree for it.
        // Handles the case where the feature branch exists locally but is not checked out anywhere.
        if (!worktreeFound && !Directory.Exists(worktreeNames.WorktreePath))
        {
            var localBranches = await _git.ListLocalBranchesAsync(
                ticketBranchName, workingDirectory, ct).ConfigureAwait(false);
            var matchingLocalBranch = localBranches.FirstOrDefault(
                b => PhaseWorktreeLayout.IsTicketBranch(b, ticketBranchName));
            if (matchingLocalBranch is not null)
            {
                ReportProgress($"[ship] creating worktree for {matchingLocalBranch}...");
                var addResult = await _git.CheckoutWorktreeAsync(
                    worktreeNames.WorktreePath, matchingLocalBranch, workingDirectory, ct).ConfigureAwait(false);
                if (addResult.Success)
                {
                    canonicalWorktreePath = worktreeNames.WorktreePath;
                    canonicalBranchName = matchingLocalBranch;
                    worktreeFound = true;
                }
            }
        }
        if (!worktreeFound)
            return (new ShipResult(false, ticketId, null,
                $"feature worktree not found at {worktreeNames.WorktreePath}",
                ShipFailureStage.StateCheck), worktreeNames, null);

        // Step 3a: Pre-flight exe-in-worktree check
        ReportProgress("[ship] pre-flight checks...");
        var exePath = _processPathProvider();
        if (exePath is not null)
        {
            var exeFull = Path.GetFullPath(exePath);
            var wtFull = Path.GetFullPath(canonicalWorktreePath);
            if (!wtFull.EndsWith(Path.DirectorySeparatorChar))
                wtFull += Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (exeFull.StartsWith(wtFull, cmp))
            {
                await _ticketing.CreateCommentAsync(ticketId,
                    $"<p><strong>ship_blocked:</strong> build.exe is running from inside the worktree being rebased ({exePath}); copy the binary to a location outside the worktree and re-run from there</p>", ct).ConfigureAwait(false);
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "pre_flight_exe_in_worktree",
                    ["exe_path"] = exePath
                }, ct).ConfigureAwait(false);
                return (new ShipResult(false, ticketId, null, $"build.exe is running from inside the worktree being rebased ({exePath}); copy the binary to a location outside the worktree and re-run from there", ShipFailureStage.PreFlight), worktreeNames, null);
            }
        }

        // Step 3b: Pre-flight dirty check - both feature and main worktrees must be clean
        // First run the precise hygiene gate to surface conflict and stash state with attribution.
        var hygieneDetail = await WorkingTreeHygieneGate.ShipPreflightAsync(
            _git, canonicalWorktreePath, workingDirectory, ticketBranchName, ct).ConfigureAwait(false);
        if (hygieneDetail is not null)
        {
            var hygieneMessage = $"working tree is not clean: {hygieneDetail}";
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> {hygieneMessage}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "pre_flight_hygiene",
                ["detail"] = hygieneDetail
            }, ct).ConfigureAwait(false);
            return (new ShipResult(false, ticketId, null, hygieneMessage, ShipFailureStage.PreFlight), worktreeNames, null);
        }

        // Fall through to the general dirty-file check for uncommitted ordinary modifications.
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
        var targetBranch = _shipOptions.TargetBranch ?? baseBranch;

        // Surface the resolved merge target and its source so a silent fallback to the
        // base branch (missing/unsaved [work].target_branch) can never go unnoticed. The
        // wording mirrors `build settarget` display mode.
        var targetSource = _shipOptions.TargetBranchOverridden ? "from [work]" : "default, no [work] override";
        ReportProgress($"[ship] target branch: {targetBranch} ({targetSource})");

        // Step 4 pre-check: the main worktree must be on the target branch before shipping.
        // FastForwardMergeAsync advances whatever is currently checked out; if the worktree is on
        // a different branch or is detached the merge lands on the wrong ref and the push sends
        // stale bytes to origin. The check is unconditional: it applies when targeting main as
        // well as when targeting a feature branch.
        {
            var currentBranch = await _git.CurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false);
            if (!string.Equals(currentBranch, targetBranch, StringComparison.Ordinal))
            {
                await _ticketing.CreateCommentAsync(ticketId,
                    $"<p><strong>ship_blocked:</strong> main worktree is on '{currentBranch}' (or detached); must be on '{targetBranch}' before shipping</p>", ct).ConfigureAwait(false);
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "wrong_worktree_branch",
                    ["expected"] = targetBranch,
                    ["actual"] = currentBranch
                }, ct).ConfigureAwait(false);
                return (new ShipResult(false, ticketId, null,
                    $"main worktree is on '{currentBranch}' (or detached); must be on '{targetBranch}' before shipping",
                    ShipFailureStage.PreFlight), worktreeNames, null);
            }
        }

        var remoteConfigured = await _git.RemoteExistsAsync(remote, workingDirectory, ct).ConfigureAwait(false);
        // Local-only mode: when push is disabled (--no-push / [ship] push=false) we never
        // touch the remote even if one is configured - no fetch, no reconcile, no push.
        var useRemote = remoteConfigured && !_shipOptions.NoPush;
        string ontoRef;
        string baseRefReason;
        if (!useRemote)
        {
            // No remote (or push disabled) - skip fetch and rebase onto local base branch
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "fetch_skipped",
                ["reason"] = remoteConfigured ? "push_disabled" : "no_remote",
                ["remote"] = remote
            }, ct).ConfigureAwait(false);
            ontoRef = targetBranch;
            baseRefReason = remoteConfigured ? "push_disabled" : "no_remote";
        }
        else
        {
            ReportProgress($"[ship] fetching from {remote}...");
            GitOpResult? fetchResult = null;
            await MainWorktreeLock.WithLockAsync(workingDirectory, async ct =>
            {
                fetchResult = await _git.FetchAsync(remote, workingDirectory, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (fetchResult == null || !fetchResult.Success)
                return (new ShipResult(false, ticketId, null,
                    $"git fetch failed: {fetchResult?.FailureReason}",
                    ShipFailureStage.Fetch), worktreeNames, null);
            ReportGitOutput("fetch output", fetchResult.RawOutput);

            // If the remote target branch has never been pushed there is nothing to
            // reconcile: rebase onto the local target and let the push (Step 8a) create
            // the branch. Without this, the ancestry checks below both fail on the
            // nonexistent <remote>/<target> ref and ship misreports a divergence (TLB-409).
            var remoteBranchExists = await _git.RemoteBranchExistsAsync(remote, targetBranch, workingDirectory, ct).ConfigureAwait(false);
            if (!remoteBranchExists)
            {
                ontoRef = targetBranch;
                baseRefReason = "remote_branch_absent";
            }
            else
            {

            // Step 4a: Determine rebase base by ancestry check
            var localRef = targetBranch;
            var remoteRef = $"{remote}/{targetBranch}";

            // Check if local is ancestor of remote (remote is ahead or equal)
            var localIsAncestorOfRemote = await _git.IsAncestorAsync(localRef, remoteRef, workingDirectory, ct).ConfigureAwait(false);
            // Check if remote is ancestor of local (local is ahead or equal)
            var remoteIsAncestorOfLocal = await _git.IsAncestorAsync(remoteRef, localRef, workingDirectory, ct).ConfigureAwait(false);

            if (localIsAncestorOfRemote && !remoteIsAncestorOfLocal)
            {
                // Remote is ahead of local
                ontoRef = remoteRef;
                baseRefReason = "origin_target_ahead";
            }
            else if (remoteIsAncestorOfLocal && !localIsAncestorOfRemote)
            {
                // Local is ahead of remote
                ontoRef = localRef;
                baseRefReason = "local_target_ahead";
            }
            else if (localIsAncestorOfRemote && remoteIsAncestorOfLocal)
            {
                // Both are ancestors of each other -> same commit
                ontoRef = remoteRef;
                baseRefReason = "same_commit";
            }
            else
            {
                // Diverged - probe for conflict subspecies before deciding
                var divergenceState = await _git.ProbeDivergenceAsync(workingDirectory, targetBranch, remote, ct).ConfigureAwait(false);

                if (divergenceState == DivergenceState.DivergedNoConflict && !_shipOptions.NoAutoMerge)
                {
                    // B02 path: auto-rebase local target branch onto origin/target branch
                    var fromSha = await _git.HeadShaAsync(workingDirectory, ct).ConfigureAwait(false);
                    var ontoSha = await _git.RevParseAsync(remoteRef, workingDirectory, ct).ConfigureAwait(false);
                    var replayedShas = await _git.LogShasAsync($"{remoteRef}..{localRef}", 0, workingDirectory, ct).ConfigureAwait(false);

                    RebaseResult? mainRebaseResult = null;
                    await MainWorktreeLock.WithLockAsync(workingDirectory, async ct =>
                    {
                        mainRebaseResult = await _git.RebaseAsync(remoteRef, workingDirectory, ct).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);

                    if (mainRebaseResult!.Success)
                    {
                        await EmitAsync(EventKind.TargetAutoRebased, ticketId, new Dictionary<string, object>
                        {
                            ["from_sha"] = fromSha,
                            ["onto_sha"] = ontoSha,
                            ["local_commits_replayed"] = replayedShas,
                            ["outcome"] = "clean"
                        }, ct).ConfigureAwait(false);
                        ontoRef = localRef;
                        baseRefReason = "auto_rebased_target";
                    }
                    else
                    {
                        // Abort unconditionally: any non-success exit from git rebase may
                        // leave HEAD detached regardless of whether conflict files were staged.
                        // RebaseAbortAsync handles the "no rebase in progress" case safely.
                        await _git.RebaseAbortAsync(workingDirectory, ct).ConfigureAwait(false);
                        await EmitAsync(EventKind.TargetAutoRebased, ticketId, new Dictionary<string, object>
                        {
                            ["from_sha"] = fromSha,
                            ["onto_sha"] = ontoSha,
                            ["local_commits_replayed"] = replayedShas,
                            ["outcome"] = "raced_to_conflict"
                        }, ct).ConfigureAwait(false);
                        await _ticketing.CreateCommentAsync(ticketId,
                            $"<p><strong>ship_blocked:</strong> local {localRef} and {remoteRef} have diverged; manual resolution required</p>", ct).ConfigureAwait(false);
                        await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                        {
                            ["kind"] = "diverged_bases",
                            ["local_ref"] = localRef,
                            ["remote_ref"] = remoteRef
                        }, ct).ConfigureAwait(false);
                        return (new ShipResult(false, ticketId, null,
                            $"local {localRef} and {remoteRef} have diverged; manual resolution required",
                            ShipFailureStage.Fetch), worktreeNames, null);
                    }
                }
                else
                {
                    await _ticketing.CreateCommentAsync(ticketId,
                        $"<p><strong>ship_blocked:</strong> local {localRef} and {remoteRef} have diverged; manual resolution required</p>", ct).ConfigureAwait(false);
                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = "diverged_bases",
                        ["local_ref"] = localRef,
                        ["remote_ref"] = remoteRef
                    }, ct).ConfigureAwait(false);
                    return (new ShipResult(false, ticketId, null,
                        $"local {localRef} and {remoteRef} have diverged; manual resolution required",
                        ShipFailureStage.Fetch), worktreeNames, null);
                }
            }
            }
        }

        // Emit base_ref_resolved event
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "base_ref_resolved",
            ["ref"] = ontoRef,
            ["reason"] = baseRefReason,
            ["target_branch"] = targetBranch,
            ["source"] = _shipOptions.TargetBranchOverridden ? "work_override" : "default"
        }, ct).ConfigureAwait(false);

        // Step 5: Rebase feature branch onto ontoRef
        ReportProgress($"[ship] rebasing {canonicalBranchName} onto {ontoRef}...");
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
        ReportProgress("[ship] scanning for conflict markers...");
        var diff = await _git.DiffAsync(ontoRef, canonicalBranchName, workingDirectory,
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
        bool regressionGateHandled = false;

        if (_shipOptions.SkipBaseline)
        {
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "baseline_skipped"
            }, ct).ConfigureAwait(false);
        }
        else if (_shipOptions.BaselineCache is not null)
        {
            if (_shipOptions.RegressionChecks.Count > 0)
                ReportProgress("[ship] computing baseline check results...");
            var baselineFailures = await ComputeBaselineAsync(ticketId, ontoRef, workingDirectory, ct).ConfigureAwait(false);
            if (baselineFailures is not null)
            {
                regressionGateHandled = true;
                if (_shipOptions.RegressionChecks.Count > 0)
                    ReportProgress($"[ship] running {_shipOptions.RegressionChecks.Count} regression check(s) on feature branch...");
                var featureResults = await _checksRunner.RunAsync(_shipOptions.RegressionChecks, canonicalWorktreePath, ct).ConfigureAwait(false);
                if (_verbose)
                {
                    foreach (var r in featureResults)
                    {
                        var status = r.Passed ? "passed" : "failed";
                        _progress?.WriteLine($"[ship] check {status}: {r.Name} ({r.Elapsed.TotalSeconds:0.0}s)");
                        if (!string.IsNullOrWhiteSpace(r.StdoutTail))
                        {
                            _progress?.WriteLine("--- stdout ---");
                            _progress?.WriteLine(r.StdoutTail.TrimEnd());
                        }
                        if (!string.IsNullOrWhiteSpace(r.StderrTail))
                        {
                            _progress?.WriteLine("--- stderr ---");
                            _progress?.WriteLine(r.StderrTail.TrimEnd());
                        }
                    }
                }

                var regressions = featureResults.Where(r => !r.Passed && !baselineFailures.Contains(r.Name)).ToList();
                var preExisting = featureResults.Where(r => !r.Passed && baselineFailures.Contains(r.Name)).ToList();
                var fixes = featureResults.Where(r => r.Passed && baselineFailures.Contains(r.Name)).ToList();

                if (preExisting.Count > 0)
                {
                    var preExistingNames = preExisting.Select(r => r.Name).ToList();
                    await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
                    {
                        ["action"] = "pre_existing_failures_noted",
                        ["count"] = preExisting.Count,
                        ["names"] = (IReadOnlyList<string>)preExistingNames
                    }, ct).ConfigureAwait(false);
                    ReportProgress($"[ship] pre-existing failures (not blocking): {string.Join(", ", preExistingNames)}");
                }

                if (fixes.Count > 0)
                {
                    var fixNames = fixes.Select(r => r.Name).ToList();
                    await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
                    {
                        ["action"] = "fixes_detected",
                        ["count"] = fixes.Count,
                        ["names"] = (IReadOnlyList<string>)fixNames
                    }, ct).ConfigureAwait(false);
                    ReportProgress($"[ship] fixed by this branch: {string.Join(", ", fixNames)}");
                }

                if (regressions.Count > 0)
                {
                    var regressionNames = regressions.Select(r => r.Name).ToList();
                    var preExistingNamesList = preExisting.Select(r => r.Name).ToList();
                    if (!_verbose)
                    {
                        foreach (var failed in regressions)
                        {
                            _progress?.WriteLine($"[ship] regression detected: {failed.Name}");
                            if (!string.IsNullOrWhiteSpace(failed.StdoutTail))
                            {
                                _progress?.WriteLine("--- stdout ---");
                                _progress?.WriteLine(failed.StdoutTail.TrimEnd());
                            }
                            if (!string.IsNullOrWhiteSpace(failed.StderrTail))
                            {
                                _progress?.WriteLine("--- stderr ---");
                                _progress?.WriteLine(failed.StderrTail.TrimEnd());
                            }
                        }
                    }
                    await _ticketing.CreateCommentAsync(ticketId,
                        $"<p><strong>ship_blocked:</strong> regression checks introduced failures: {string.Join(", ", regressionNames)}</p>", ct).ConfigureAwait(false);
                    await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                    {
                        ["kind"] = "regression_checks",
                        ["regressions"] = (IReadOnlyList<string>)regressionNames,
                        ["pre_existing"] = (IReadOnlyList<string>)preExistingNamesList
                    }, ct).ConfigureAwait(false);
                    return (new ShipResult(false, ticketId, null,
                        $"regression checks introduced failures: {string.Join(", ", regressionNames)}", ShipFailureStage.RegressionChecks), worktreeNames, null);
                }
            }
            // else: baseline computation failed, fall through to legacy
        }

        if (!regressionGateHandled)
        {
            // Legacy regression check: any failing test blocks ship
            if (_shipOptions.RegressionChecks.Count > 0)
                ReportProgress($"[ship] running {_shipOptions.RegressionChecks.Count} regression check(s)...");
            var checkResults = await _checksRunner.RunAsync(_shipOptions.RegressionChecks, canonicalWorktreePath, ct).ConfigureAwait(false);
            if (_verbose)
            {
                foreach (var r in checkResults)
                {
                    var status = r.Passed ? "passed" : "failed";
                    _progress?.WriteLine($"[ship] check {status}: {r.Name} ({r.Elapsed.TotalSeconds:0.0}s)");
                    if (!string.IsNullOrWhiteSpace(r.StdoutTail))
                    {
                        _progress?.WriteLine("--- stdout ---");
                        _progress?.WriteLine(r.StdoutTail.TrimEnd());
                    }
                    if (!string.IsNullOrWhiteSpace(r.StderrTail))
                    {
                        _progress?.WriteLine("--- stderr ---");
                        _progress?.WriteLine(r.StderrTail.TrimEnd());
                    }
                }
            }

            var checksFailed = checkResults.Where(r => !r.Passed).ToList();
            if (checksFailed.Count > 0)
            {
                var namesList = string.Join(", ", checksFailed.Select(r => r.Name));
                if (!_verbose)
                {
                    foreach (var failed in checksFailed)
                    {
                        _progress?.WriteLine($"[ship] regression check failed: {failed.Name}");
                        if (!string.IsNullOrWhiteSpace(failed.StdoutTail))
                        {
                            _progress?.WriteLine("--- stdout ---");
                            _progress?.WriteLine(failed.StdoutTail.TrimEnd());
                        }
                        if (!string.IsNullOrWhiteSpace(failed.StderrTail))
                        {
                            _progress?.WriteLine("--- stderr ---");
                            _progress?.WriteLine(failed.StderrTail.TrimEnd());
                        }
                    }
                }
                await _ticketing.CreateCommentAsync(ticketId,
                    $"<p><strong>ship_blocked:</strong> regression checks failed: {namesList}</p>", ct).ConfigureAwait(false);
                await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
                {
                    ["kind"] = "regression_checks",
                    ["checks_failed"] = checksFailed.Select(r => r.Name).ToList()
                }, ct).ConfigureAwait(false);
                return (new ShipResult(false, ticketId, null,
                    $"regression checks failed: {namesList}", ShipFailureStage.RegressionChecks), worktreeNames, null);
            }
        }

        // Step 8: Fast-forward merge into local targetBranch (main worktree)
        ReportProgress($"[ship] merging into {targetBranch}...");
        GitOpResult? ffResult = null;
        string? headAfterMerge = null;
        await MainWorktreeLock.WithLockAsync(workingDirectory, async ct =>
        {
            ffResult = await _git.FastForwardMergeAsync(canonicalBranchName, workingDirectory, ct).ConfigureAwait(false);
            // Post-condition: verify HEAD is still on the local target branch. Checked
            // inside the lock so no other ship can change HEAD between merge and check.
            if (ffResult!.Success)
                headAfterMerge = await _git.CurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (ffResult == null || !ffResult.Success)
            return (new ShipResult(false, ticketId, null,
                $"fast-forward merge failed: {ffResult?.FailureReason}",
                ShipFailureStage.FastForwardMerge), worktreeNames, null);
        if (!string.Equals(headAfterMerge, targetBranch, StringComparison.Ordinal))
            return (new ShipResult(false, ticketId, null,
                $"HEAD is on '{headAfterMerge}' after ff-merge; expected '{targetBranch}'",
                ShipFailureStage.FastForwardMerge), worktreeNames, null);
        ReportGitOutput("merge output", ffResult?.RawOutput);

        // Step 8a: Push to remote (skipped when no remote is configured or push is disabled).
        // A first-time push here creates the target branch when it did not exist on the remote.
        if (useRemote)
        {
            ReportProgress($"[ship] pushing to {remote}/{targetBranch}...");
            var pushResult = await _git.PushAsync(remote, targetBranch, workingDirectory, ct).ConfigureAwait(false);
            if (!pushResult.Success)
                return (new ShipResult(false, ticketId, null,
                    $"git push failed: {pushResult.FailureReason}",
                    ShipFailureStage.Push), worktreeNames, null);
            ReportGitOutput("push output", pushResult.RawOutput);
        }

        // Step 9: Read merged HEAD sha
        var mergedSha = await _git.HeadShaAsync(workingDirectory, ct).ConfigureAwait(false);

        // Step 10: Post shipped_at comment
        ReportProgress($"[ship] updating ticket {ticketId}...");
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

        // Step 12: Decruft worktree. Skipped when SkipDecruft is set (the chain owns the
        // shared worktree and will remove it once after all tickets complete). Failure must
        // not unwind Done in any case.
        if (!_shipOptions.SkipDecruft)
        {
            ReportProgress("[ship] cleaning up worktree...");
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
        }

        // Step 13: Optionally delete feature branch. Failure does not unwind Done.
        // Force-delete (-D), not -d: Step 8 already fast-forward-merged this branch into the
        // local target, so it is provably merged. `git branch -d` instead checks the branch's
        // configured upstream (origin/<target>), and when that ref lags local target - push
        // disabled, or origin behind - it refuses with "not fully merged to origin/main, even
        // though merged to HEAD" and the merged branch leaks. The local merge is the ship's
        // source of truth here, so -D is correct.
        if (_shipOptions.DeleteFeatureBranch)
        {
            var deleteResult = await _git.DeleteBranchAsync(canonicalBranchName, force: true, workingDirectory, ct).ConfigureAwait(false);
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

    private async Task<ShipResult> RunParentShipAsync(
        string ticketId,
        Ticket ticket,
        IReadOnlyList<Ticket> children,
        CancellationToken ct)
    {
        // Find children not in Done state
        var notDone = children.Where(c => c.State != TicketState.Done).ToList();

        if (notDone.Count > 0)
        {
            var blockerIds = string.Join(", ", notDone.Select(c => c.Id));
            var message = $"children not Done: {blockerIds}";
            await _ticketing.CreateCommentAsync(ticketId,
                $"<p><strong>ship_blocked:</strong> {message}</p>", ct).ConfigureAwait(false);
            await EmitAsync(EventKind.GateFailure, ticketId, new Dictionary<string, object>
            {
                ["kind"] = "parent_children_not_done",
                ["blockers"] = (IReadOnlyList<string>)notDone.Select(c => c.Id).ToList()
            }, ct).ConfigureAwait(false);
            return new ShipResult(false, ticketId, null, message, ShipFailureStage.StateCheck);
        }

        // All children Done - transition parent to Done
        await _ticketing.TransitionAsync(ticketId, TicketState.Done, ct).ConfigureAwait(false);
        await EmitAsync(EventKind.StateTransition, ticketId, new Dictionary<string, object>
        {
            ["from"] = ticket.State.ToString(),
            ["to"] = "Done"
        }, ct).ConfigureAwait(false);

        await _ticketing.CreateCommentAsync(ticketId,
            $"<p><strong>shipped:</strong> all {children.Count} children are Done; parent transitioned to Done</p>", ct).ConfigureAwait(false);
        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "create_comment"
        }, ct).ConfigureAwait(false);

        return new ShipResult(true, ticketId, null, null, null);
    }

    private async Task<IReadOnlySet<string>?> ComputeBaselineAsync(
        string ticketId,
        string ontoRef,
        string workingDirectory,
        CancellationToken ct)
    {
        var ontoSha = await _git.RevParseAsync(ontoRef, workingDirectory, ct).ConfigureAwait(false);
        var cache = _shipOptions.BaselineCache!;
        if (cache.TryGet(ontoSha, out var cached))
            return cached;

        var baselinePath = Path.Combine(workingDirectory, ".worktrees", $"baseline-{ontoSha[..8]}");
        var createResult = await _git.CreateDetachedWorktreeAsync(baselinePath, ontoSha, workingDirectory, ct).ConfigureAwait(false);
        if (!createResult.Success)
        {
            await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
            {
                ["action"] = "baseline_worktree_failed",
                ["reason"] = createResult.FailureReason ?? "unknown"
            }, ct).ConfigureAwait(false);
            return null;
        }

        var baselineResults = await _checksRunner.RunAsync(_shipOptions.RegressionChecks, baselinePath, ct).ConfigureAwait(false);
        var failing = baselineResults.Where(r => !r.Passed).Select(r => r.Name).ToHashSet();
        var failingSet = (IReadOnlySet<string>)failing;
        cache.Set(ontoSha, failingSet);

        await EmitAsync(EventKind.TicketWrite, ticketId, new Dictionary<string, object>
        {
            ["action"] = "baseline_computed",
            ["sha"] = ontoSha,
            ["failing_count"] = failing.Count
        }, ct).ConfigureAwait(false);

        try
        {
            await _decrufter.DecruftAsync(baselinePath, workingDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            // fire-and-forget: baseline worktree cleanup failure does not block ship
        }

        return failingSet;
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
