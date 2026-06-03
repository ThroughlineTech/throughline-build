using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

internal static class CommentMarkers
{
    // Returns the value of the most recently created comment marker with the given name,
    // or null when no comment carries it.
    //
    // Chain re-runs accumulate multiple [planned_at]/[implemented_at] markers on the same
    // ticket - each run posts its own. The marker that matters is always the freshest one
    // (the current run's). Selecting by comment creation time makes that deterministic
    // regardless of the order GetCommentsAsync returns comments in. The previous call sites
    // scanned by list position instead: ReviewPhase kept the LAST marker in return order and
    // Plane returns comments newest-first, so a re-run read a STALE implemented_at from a
    // prior run (an orphaned commit on a different base) and mis-attributed the implementer
    // diff / automated-check state - surfacing as a spurious Rework (TLB-412).
    public static string? LatestValue(IReadOnlyList<TicketComment> comments, string markerName)
    {
        string? value = null;
        var best = DateTimeOffset.MinValue;
        foreach (var comment in comments)
        {
            foreach (var marker in MarkerParser.Parse(comment.Body))
            {
                if (marker.Name == markerName &&
                    !string.IsNullOrEmpty(marker.Value) &&
                    comment.CreatedAt >= best)
                {
                    best = comment.CreatedAt;
                    value = marker.Value;
                }
            }
        }
        return value;
    }
}
