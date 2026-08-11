namespace ThroughlineBuild.Git;

/// <summary>
/// Whether the index could be consulted, and if so what it said. The distinction matters to any
/// caller that treats "not tracked" as evidence rather than as a hint: an absent git binary and
/// an empty index both yield no paths, but only one of them is an answer.
/// </summary>
public enum GitTrackedPathScope
{
    /// <summary>git answered; <see cref="GitTrackedPathProbe.Paths"/> is the complete tracked set.</summary>
    Tracked,

    /// <summary>git ran and reported no repository here, so there is no index to consult.</summary>
    NotARepository,

    /// <summary>git could not be run or failed for some other reason; the question is unanswered.</summary>
    Unavailable,
}

/// <summary>
/// Result of asking the index which catalog paths are tracked. <see cref="Paths"/> is empty for
/// every scope except <see cref="GitTrackedPathScope.Tracked"/>, so callers must branch on
/// <see cref="Scope"/> before reading it as "nothing is tracked".
/// </summary>
public sealed record GitTrackedPathProbe(
    GitTrackedPathScope Scope,
    IReadOnlyList<string> Paths,
    string? Failure)
{
    public static GitTrackedPathProbe Tracked(IReadOnlyList<string> paths) =>
        new(GitTrackedPathScope.Tracked, paths, null);

    public static GitTrackedPathProbe NotARepository() =>
        new(GitTrackedPathScope.NotARepository, Array.Empty<string>(), null);

    public static GitTrackedPathProbe Unavailable(string failure) =>
        new(GitTrackedPathScope.Unavailable, Array.Empty<string>(), failure);
}
