namespace ThroughlineBuild.Contracts;

public record WorktreeInfo(
    string Path,
    string Branch,
    string HeadSha,
    bool IsLocked,
    bool IsPrunable);

public record WorktreeRemoveResult(bool Success, string? FailureReason);

public record WorktreeCreateResult(bool Success, string? FailureReason, string? AbsolutePath);

public record GitOpResult(bool Success, string? FailureReason, string? RawOutput = null);

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

    // Checks out an existing local branch into a new worktree (git worktree add <path> <branch>).
    // Differs from CreateWorktreeAsync which always creates a new branch (-b flag).
    // Default returns failure so existing test fakes remain unchanged.
    Task<WorktreeCreateResult> CheckoutWorktreeAsync(string worktreePath, string existingBranch, string mainWorktreePath, CancellationToken ct) =>
        Task.FromResult(new WorktreeCreateResult(false, "not implemented", null));
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

    // Lists local branches matching a glob pattern (git branch --list <pattern>).
    // Default returns empty so existing FakeGitClients remain unchanged.
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> ListLocalBranchesAsync(string pattern, string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Returns true if a remote with the given name is configured in the repo.
    // Default returns true so existing FakeGitClients remain unchanged (TLB-127).
    // Never throws - treat any error as false.
    Task<bool> RemoteExistsAsync(string remote, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(true);

    // Returns true if the remote-tracking branch <remote>/<branch> exists locally
    // (i.e. the target branch has been fetched/pushed at least once). Distinguishes
    // "branch never pushed" from "branch diverged" so ShipPhase does not misclassify
    // an unpushed target as a divergence (TLB-409).
    // Default returns true so existing FakeGitClients keep their reconcile path.
    // Never throws - treat any error as false.
    Task<bool> RemoteBranchExistsAsync(string remote, string branch, string workingDirectory, CancellationToken ct) =>
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

    // Returns SHAs of commits in the given range (e.g. "origin/main..main"), newest first.
    // limit 0 means no limit. Default returns empty so existing FakeGitClients remain unchanged (TLB-298).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> LogShasAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Returns the name of the currently checked-out branch in workingDirectory.
    // Runs "git rev-parse --abbrev-ref HEAD".
    // Default returns "main" so existing FakeGitClients remain unchanged (TLB-349).
    // Never throws - returns "main" on git failure.
    Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct) =>
        Task.FromResult("main");

    // Returns paths that have unmerged / conflict status codes in git status --porcelain.
    // Conflict codes are: DD, AU, UD, UA, DU, AA, UU (both columns contain U, A, or D in conflict combinations).
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-374).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> GetConflictedPathsAsync(string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Creates a new branch from fromRef inside an existing worktree (git checkout -b <branch> <fromRef>).
    // Used by the shared-chain-worktree path to set up each ticket's branch without a new worktree add.
    // Default returns failure so existing test fakes remain unchanged.
    // Never throws - failure is returned as GitOpResult(false, ...).
    Task<GitOpResult> CreateBranchAsync(string branch, string fromRef, string worktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(false, "not implemented"));

    // Switches an existing worktree onto an already-existing local branch (git switch <branch>).
    // Differs from CreateBranchAsync (checkout -b, new branch) and CheckoutWorktreeAsync (new
    // worktree). Used by the batch chain ship: a warm batch session leaves the integration worktree
    // on the batch branch, so the ship switches it back to the integration branch before
    // fast-forwarding that branch onto the batch stack tip.
    // Default returns success (no-op) so FakeGitClients that do not model branch HEADs keep working.
    // Never throws - failure is returned as GitOpResult(false, ...).
    Task<GitOpResult> SwitchBranchAsync(string branch, string worktreePath, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(true, null));

    // Returns raw stash list lines from "git stash list" (e.g. "stash@{0}: On ticket/tlb-1-foo: WIP").
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-374).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> ListStashEntriesAsync(string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Drops a single stash entry (git stash drop <stashRef>) from the repo-global stack.
    // Used by ReviewPhase to remove stash entries a verifier pushed, since the stash stack
    // leaks across worktrees and would otherwise corrupt a later ticket (TLB-478).
    // Default returns failure so existing FakeGitClients remain unchanged.
    // Never throws - failure is returned as GitOpResult(false, ...).
    Task<GitOpResult> StashDropAsync(string stashRef, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(new GitOpResult(false, "not implemented"));

    // Creates a detached-HEAD worktree at a specific commit (git worktree add --detach <path> <sha>).
    // No branch is created; cleanup is a plain worktree remove.
    // Default returns failure so existing fakes remain unchanged (TLB-401).
    Task<WorktreeCreateResult> CreateDetachedWorktreeAsync(
        string worktreePath,
        string sha,
        string mainWorktreePath,
        CancellationToken ct) =>
        Task.FromResult(new WorktreeCreateResult(false, "not implemented", null));

    // Returns the distinct file paths touched by commits in the given range via "git diff --stat".
    // The range should be a two-dot range (e.g. "abc123..def456") covering exactly the chain's prior work.
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-379).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Returns untracked file paths in workingDirectory (git ls-files --others --exclude-standard).
    // Ignored files (matched by .gitignore) are excluded from the result.
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-489).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> GetUntrackedFilesAsync(string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // Returns the subset of paths (relative to repo root) that are tracked (present in the index)
    // of the git repository rooted at workingDirectory. Empty input returns empty without running git.
    // Default returns empty so existing FakeGitClients remain unchanged (TLB-489).
    // Never throws - returns empty on git failure.
    Task<IReadOnlyList<string>> FilterTrackedPathsAsync(IReadOnlyList<string> paths, string workingDirectory, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
