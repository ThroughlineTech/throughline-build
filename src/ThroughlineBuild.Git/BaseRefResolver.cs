using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Git;

public static class BaseRefResolver
{
    // Resolves the base ref used by plan/implement/review (worktree source, diff base,
    // planned_at marker). Prefers origin/main; falls back to local main for repos that
    // have no origin remote (e.g. fresh local-only repos). Returns both the resolved
    // ref name and its SHA so callers can pass the ref name to downstream git commands
    // (worktree create, diff) and the SHA to brief builders / drift checks.
    public static async Task<(string RefName, string Sha)> ResolveAsync(
        IGitClient git, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var sha = await git.RevParseAsync("origin/main", workingDirectory, ct).ConfigureAwait(false);
            return ("origin/main", sha);
        }
        catch
        {
            var sha = await git.RevParseAsync("main", workingDirectory, ct).ConfigureAwait(false);
            return ("main", sha);
        }
    }
}
