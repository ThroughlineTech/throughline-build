using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Git;

public static class BaseRefResolver
{
    // Resolves the base ref used by plan/implement/review (worktree source, diff base,
    // planned_at marker). Prefers origin/<targetBranch>; falls back to the supplied
    // target branch for repos that have no origin remote (e.g. fresh local-only repos).
    //
    // Accumulation rule (TLB-411): when origin/<targetBranch> exists but the LOCAL
    // <targetBranch> is strictly ahead of it (origin is an ancestor of local, local is not
    // an ancestor of origin), the local branch is preferred. This is the state a chain that
    // ships locally (--no-push) is in: each shipped sibling fast-forwards local <targetBranch>
    // while origin/<targetBranch> stays frozen. Cutting the next ticket from the accumulating
    // local tip is what lets chain children stack on each other instead of all branching from
    // the frozen origin and then colliding at ship-time rebase. When local equals origin, is
    // behind it, or has diverged, origin stays the canonical base (ShipPhase reconciles real
    // divergence separately via its own ancestry probe).
    //
    // Returns both the resolved ref name and its SHA so callers can pass the ref name to
    // downstream git commands (worktree create, diff) and the SHA to brief builders / drift
    // checks.
    public static async Task<(string RefName, string Sha)> ResolveAsync(
        IGitClient git, string workingDirectory, string targetBranch, CancellationToken ct)
    {
        var remoteRef = $"origin/{targetBranch}";
        string remoteSha;
        try
        {
            remoteSha = await git.RevParseAsync(remoteRef, workingDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            // No origin remote (or origin/<target> never fetched): fall back to the local
            // target branch.
            try
            {
                var localOnlySha = await git.RevParseAsync(targetBranch, workingDirectory, ct).ConfigureAwait(false);
                return (targetBranch, localOnlySha);
            }
            catch
            {
                // Neither origin/<target> nor local <target> resolves. Surface an actionable
                // error instead of git's raw "ambiguous argument '<target>'" plumbing message,
                // distinguishing a repo with no commits at all (unborn HEAD) from a repo whose
                // base branch is simply named differently.
                throw await BuildUnresolvableBaseErrorAsync(git, workingDirectory, targetBranch, ct)
                    .ConfigureAwait(false);
            }
        }

        // origin/<target> exists. Prefer the local branch only when it is strictly ahead.
        try
        {
            var localSha = await git.RevParseAsync(targetBranch, workingDirectory, ct).ConfigureAwait(false);
            if (!string.Equals(localSha, remoteSha, StringComparison.Ordinal))
            {
                var originIsAncestorOfLocal =
                    await git.IsAncestorAsync(remoteRef, targetBranch, workingDirectory, ct).ConfigureAwait(false);
                var localIsAncestorOfOrigin =
                    await git.IsAncestorAsync(targetBranch, remoteRef, workingDirectory, ct).ConfigureAwait(false);
                if (originIsAncestorOfLocal && !localIsAncestorOfOrigin)
                    return (targetBranch, localSha);
            }
        }
        catch
        {
            // Local target branch absent or ancestry probe failed - keep origin as the base.
        }

        return (remoteRef, remoteSha);
    }

    // Builds the actionable exception thrown when the base branch cannot be resolved at all.
    // Probes HEAD to tell "repo has no commits yet" apart from "target branch named differently"
    // so the operator sees a fix-it instruction rather than git's "ambiguous argument" plumbing.
    private static async Task<InvalidOperationException> BuildUnresolvableBaseErrorAsync(
        IGitClient git, string workingDirectory, string targetBranch, CancellationToken ct)
    {
        bool hasCommits;
        try
        {
            await git.RevParseAsync("HEAD", workingDirectory, ct).ConfigureAwait(false);
            hasCommits = true;
        }
        catch
        {
            hasCommits = false;
        }

        return hasCommits
            ? new InvalidOperationException(
                $"base branch '{targetBranch}' does not exist in {workingDirectory}. " +
                "Create it, or point build at the right branch with 'build settarget <branch>'.")
            : new InvalidOperationException(
                $"repository at {workingDirectory} has no commits yet, so base branch " +
                $"'{targetBranch}' cannot be resolved. Create an initial commit before running build " +
                "(e.g. 'git commit --allow-empty -m init').");
    }
}
