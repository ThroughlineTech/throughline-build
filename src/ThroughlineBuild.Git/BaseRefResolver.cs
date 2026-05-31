using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Git;

public static class BaseRefResolver
{
    // Resolves the base ref used by plan/implement/review (worktree source, diff base,
    // planned_at marker). Prefers origin/<targetBranch>; falls back to the supplied
    // target branch for repos that have no origin remote (e.g. fresh local-only repos).
    // Returns both the resolved ref name and its SHA so callers can pass the ref name
    // to downstream git commands (worktree create, diff) and the SHA to brief builders
    // / drift checks.
    public static async Task<(string RefName, string Sha)> ResolveAsync(
        IGitClient git, string workingDirectory, string targetBranch, CancellationToken ct)
    {
        var remoteRef = $"origin/{targetBranch}";
        try
        {
            var sha = await git.RevParseAsync(remoteRef, workingDirectory, ct).ConfigureAwait(false);
            return (remoteRef, sha);
        }
        catch
        {
            var sha = await git.RevParseAsync(targetBranch, workingDirectory, ct).ConfigureAwait(false);
            return (targetBranch, sha);
        }
    }
}
