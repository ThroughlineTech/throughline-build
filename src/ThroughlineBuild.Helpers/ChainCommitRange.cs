using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Helpers;

/// <summary>
/// Describes the commits the chain has already produced: the range anchored to the
/// SHA captured at chain start, the count of commits in that range, and the distinct
/// file paths touched by those commits (derived from git diff --stat).
///
/// When no tickets have shipped yet (i.e. chainStartSha equals currentTargetSha)
/// the range is empty: CommitCount=0 and TouchedFiles is empty.
/// </summary>
public record ChainCommitRange(
    string StartSha,
    string EndSha,
    int CommitCount,
    IReadOnlyList<string> TouchedFiles)
{
    /// <summary>True when the chain has no prior commits (first ticket, or anchors are equal).</summary>
    public bool IsEmpty => CommitCount == 0;
}

public static class ChainCommitRangeHelper
{
    /// <summary>
    /// Computes the chain's commit range from <paramref name="chainStartSha"/> to
    /// <paramref name="currentTargetSha"/> using <paramref name="git"/>.
    ///
    /// Makes no LLM call and spawns no worker. Best-effort: on any git failure the
    /// returned range has CommitCount=0 and empty TouchedFiles.
    /// </summary>
    public static async Task<ChainCommitRange> ComputeAsync(
        IGitClient git,
        string chainStartSha,
        string currentTargetSha,
        string workingDirectory,
        CancellationToken ct)
    {
        // If anchors are identical, the chain has not shipped any tickets yet.
        if (string.Equals(chainStartSha, currentTargetSha, StringComparison.OrdinalIgnoreCase))
        {
            return new ChainCommitRange(
                StartSha: chainStartSha,
                EndSha: currentTargetSha,
                CommitCount: 0,
                TouchedFiles: Array.Empty<string>());
        }

        // Two-dot range: commits reachable from currentTargetSha but not from chainStartSha.
        var range = $"{chainStartSha}..{currentTargetSha}";

        var count = await git.RevListCountAsync(range, workingDirectory, ct).ConfigureAwait(false);

        if (count == 0)
        {
            // No commits in range (e.g. chainStartSha is ahead of currentTargetSha, or
            // the refs are identical after normalization). Treat as empty.
            return new ChainCommitRange(
                StartSha: chainStartSha,
                EndSha: currentTargetSha,
                CommitCount: 0,
                TouchedFiles: Array.Empty<string>());
        }

        // git diff --name-only <start>..<end> lists the files touched across the range.
        var rawFiles = await git.DiffStatFilesAsync(range, workingDirectory, ct).ConfigureAwait(false);

        // Deduplicate while preserving first-encounter order.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var touched = new List<string>(rawFiles.Count);
        foreach (var f in rawFiles)
        {
            if (seen.Add(f))
                touched.Add(f);
        }

        return new ChainCommitRange(
            StartSha: chainStartSha,
            EndSha: currentTargetSha,
            CommitCount: count,
            TouchedFiles: touched.AsReadOnly());
    }
}
