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
}
