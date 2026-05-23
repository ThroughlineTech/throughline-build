namespace ThroughlineBuild.Contracts;

public record WorktreeInfo(
    string Path,
    string Branch,
    string HeadSha,
    bool IsLocked,
    bool IsPrunable);

public record WorktreeRemoveResult(bool Success, string? FailureReason);

public interface IGitClient
{
    Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct);
    Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct);
    Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct);
    Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct);
}
