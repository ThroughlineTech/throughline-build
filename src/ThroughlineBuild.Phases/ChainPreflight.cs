using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Runs the ordered, outermost-chain checks that must pass before any phase work begins.
/// </summary>
public sealed class ChainPreflight
{
    private const int DirtyPathSampleLimit = 25;

    private readonly IGitClient _git;
    private readonly string _workingDirectory;
    private readonly string _targetBranch;

    public ChainPreflight(
        IGitClient git,
        string workingDirectory,
        string targetBranch)
    {
        _git = git;
        _workingDirectory = workingDirectory;
        _targetBranch = targetBranch;
    }

    /// <summary>
    /// Returns the first refusal in preflight order, or <see langword="null"/> when all checks pass.
    /// Event emission remains the caller's responsibility.
    /// </summary>
    public async Task<ChainPreflightRefusal?> CheckAsync(
        string ticketBranchName,
        CancellationToken ct)
    {
        var currentBranch = await _git
            .CurrentBranchAsync(_workingDirectory, ct)
            .ConfigureAwait(false);
        if (!string.Equals(currentBranch, _targetBranch, StringComparison.Ordinal))
        {
            var message =
                $"{_workingDirectory} is on '{currentBranch}' (or detached); the chain ships into " +
                $"'{_targetBranch}', so the main worktree must be on '{_targetBranch}' before starting. " +
                $"Switch with 'git switch {_targetBranch}' and re-run.";

            return new ChainPreflightRefusal(
                ChainOutcome.RefusedWrongBranch,
                message,
                DirtyTreeCause: null,
                EventData: new Dictionary<string, object>
                {
                    ["kind"] = "chain_preflight_wrong_branch",
                    ["expected"] = _targetBranch,
                    ["actual"] = currentBranch,
                    ["worktree"] = _workingDirectory
                });
        }

        var hygieneFailure = await WorkingTreeHygieneGate
            .CheckAsync(_git, _workingDirectory, ticketBranchName, ct)
            .ConfigureAwait(false);
        if (hygieneFailure is not null)
        {
            return new ChainPreflightRefusal(
                ChainOutcome.RefusedDirtyTree,
                hygieneFailure,
                DirtyTreeCause.Hygiene,
                new Dictionary<string, object>
                {
                    ["kind"] = "hygiene_gate_preflight",
                    ["detail"] = hygieneFailure
                });
        }

        var dirtyTrackedPaths = await _git
            .GetTrackedChangesAsync(_workingDirectory, ct)
            .ConfigureAwait(false);
        if (dirtyTrackedPaths.Count == 0)
            return null;

        var dirtyPathSample = dirtyTrackedPaths.Take(DirtyPathSampleLimit).ToList();
        var dirtyPathList = string.Join(", ", dirtyPathSample);
        var more = dirtyTrackedPaths.Count > dirtyPathSample.Count
            ? $" (+{dirtyTrackedPaths.Count - dirtyPathSample.Count} more)"
            : "";
        var dirtyMessage =
            $"{_workingDirectory} has {dirtyTrackedPaths.Count} modified tracked files: " +
            $"{dirtyPathList}{more}. Commit, stash, or revert them before running build chain.";

        return new ChainPreflightRefusal(
            ChainOutcome.RefusedDirtyTree,
            dirtyMessage,
            DirtyTreeCause.TrackedChanges,
            new Dictionary<string, object>
            {
                ["kind"] = "chain_preflight_dirty",
                ["dirty_count"] = dirtyTrackedPaths.Count,
                ["dirty_paths"] = dirtyPathSample,
                ["worktree"] = _workingDirectory
            });
    }
}

/// <summary>
/// Describes a chain preflight refusal and the unchanged gate-failure event payload.
/// </summary>
public sealed record ChainPreflightRefusal(
    ChainOutcome Outcome,
    string Message,
    DirtyTreeCause? DirtyTreeCause,
    IReadOnlyDictionary<string, object> EventData);
