namespace ThroughlineBuild.Contracts;

public record WorktreeInfo(
    string Path,
    string Branch,
    string HeadSha,
    bool IsLocked,
    bool IsPrunable);

public record WorktreeRemoveResult(bool Success, string? FailureReason);

public record WorktreeCreateResult(bool Success, string? FailureReason, string? AbsolutePath);

public record GitOpResult(bool Success, string? FailureReason);

public record RebaseResult(
    bool Success,
    bool HadConflicts,
    IReadOnlyList<string> ConflictingPaths,
    string? FailureReason);

public enum DivergenceState
{
    Clean,
    LocalAhead,
    RemoteAhead,
    DivergedNoConflict,
    DivergedWithConflict
}

public interface IGitClient
{
    Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct);
    Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct);
    Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct);
    Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct);
    Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct);
    Task<string> HeadShaAsync(string worktreePath, CancellationToken ct);
    Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct);
    Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct);
    Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct);
    Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct);
    Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct);
    Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct);

    // Read-only helpers added for phase-completion summaries (TLB-123).
    // Both return best-effort values: 0 / empty list on git failure, never throw.
    Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct);
    Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct);

    // Returns true if a remote with the given name is configured in the repo.
    // Default returns true so existing FakeGitClients remain unchanged (TLB-127).
    // Never throws - treat any error as false.
    Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(true);

    // Returns the list of tracked files with uncommitted changes in workingDirectory.
    // Runs "git status --porcelain" and returns lines that are not untracked (i.e. not "??").
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-131).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Returns true if ancestor is an ancestor of descendant (i.e., descendant is reachable from ancestor).
    // Returns false if descendant is not reachable from ancestor OR if either ref does not exist.
    // Never throws - returns false on git failure.
    // Used to check if local main and origin/main have diverged (TLB-148).
    Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(false);

    // Pushes the specified local branch to the remote.
    // Default returns success so existing test fakes remain unchanged (TLB-293).
    Task<GitOpResult> PushAsync(string remote, string branch, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    // Returns the divergence category between local baseBranch and remote/baseBranch.
    // Safe default is DivergedWithConflict so existing fakes remain unchanged (TLB-296).
    // Never throws - any error returns DivergedWithConflict.
    Task<DivergenceState> ProbeDivergenceAsync(string mainWorktreePath, string baseBranch, string remote, CancellationToken ct) =>
        Task.FromResult(DivergenceState.DivergedWithConflict);
}
