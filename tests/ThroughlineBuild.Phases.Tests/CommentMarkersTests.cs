using ThroughlineBuild.Contracts;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class CommentMarkersTests
{
    private static TicketComment C(string html, DateTimeOffset at) =>
        new TicketComment(Guid.NewGuid().ToString(), html, at);

    [Fact]
    public void LatestValue_PicksMarkerFromMostRecentComment_RegardlessOfListOrder()
    {
        var now = DateTimeOffset.UtcNow;
        // Newest-first list order (as Plane returns), but the freshest by timestamp is first here.
        var comments = new[]
        {
            C("<p>[implemented_at: fresh]</p>", now),
            C("<p>[implemented_at: stale]</p>", now.AddDays(-2)),
            C("<p>[implemented_at: older]</p>", now.AddDays(-5)),
        };

        Assert.Equal("fresh", CommentMarkers.LatestValue(comments, "implemented_at"));
    }

    [Fact]
    public void LatestValue_FreshestMarkerLastInList_StillWins()
    {
        var now = DateTimeOffset.UtcNow;
        var comments = new[]
        {
            C("<p>[planned_at: stale]</p>", now.AddDays(-3)),
            C("<p>[planned_at: fresh]</p>", now),
        };

        Assert.Equal("fresh", CommentMarkers.LatestValue(comments, "planned_at"));
    }

    [Fact]
    public void LatestValue_IgnoresOtherMarkerNames()
    {
        var now = DateTimeOffset.UtcNow;
        var comments = new[]
        {
            C("<p>[planned_at: p1]</p>", now),
            C("<p>[implemented_at: i1]</p>", now.AddDays(-1)),
        };

        Assert.Equal("i1", CommentMarkers.LatestValue(comments, "implemented_at"));
        Assert.Equal("p1", CommentMarkers.LatestValue(comments, "planned_at"));
    }

    [Fact]
    public void LatestValue_NoMatchingMarker_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var comments = new[] { C("<p>nothing here</p>", now) };

        Assert.Null(CommentMarkers.LatestValue(comments, "implemented_at"));
    }

    [Fact]
    public void LatestValue_EmptyComments_ReturnsNull()
    {
        Assert.Null(CommentMarkers.LatestValue(Array.Empty<TicketComment>(), "implemented_at"));
    }
}
