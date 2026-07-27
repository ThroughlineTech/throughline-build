using ThroughlineBuild.EventLog;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

public class SessionFileNameBuilderTests
{
    private static readonly DateTimeOffset FixedTs = new(2026, 5, 28, 14, 30, 52, TimeSpan.Zero);

    [Fact]
    public void Build_PhaseVerb_WithTicket_Shape()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "Throughline Build",
            projectIdentifier: "TLB",
            verb: "implement",
            ticketId: "TLB-169",
            extraSlug: null,
            timestamp: FixedTs);

        Assert.Equal("throughline-build-TLB-169-implement-2026-05-28-143052", stem);
    }

    [Fact]
    public void Build_PreservesTicketIdCase()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "Throughline Build",
            projectIdentifier: null,
            verb: "plan",
            ticketId: "TLB-42",
            extraSlug: null,
            timestamp: FixedTs);

        Assert.Contains("TLB-42", stem);
    }

    [Fact]
    public void Build_FallsBackToIdentifier_WhenProjectNameEmpty()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "",
            projectIdentifier: "TLB",
            verb: "implement",
            ticketId: "TLB-1",
            extraSlug: null,
            timestamp: FixedTs);

        Assert.StartsWith("tlb-", stem);
    }

    [Fact]
    public void Build_OmitsProjectToken_WhenBothNullOrEmpty()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: null,
            projectIdentifier: null,
            verb: "implement",
            ticketId: "TLB-1",
            extraSlug: null,
            timestamp: FixedTs);

        Assert.Equal("TLB-1-implement-2026-05-28-143052", stem);
    }

    [Fact]
    public void Build_NewVerb_NoTicket_UsesProjectVerbDateTime()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "Throughline Build",
            projectIdentifier: "TLB",
            verb: "new",
            ticketId: null,
            extraSlug: null,
            timestamp: FixedTs);

        Assert.Equal("throughline-build-new-2026-05-28-143052", stem);
    }

    [Fact]
    public void Build_ScaffoldVerb_UsesExtraSlugWhenNoTicket()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "Throughline Build",
            projectIdentifier: "TLB",
            verb: "scaffold",
            ticketId: null,
            extraSlug: "op-13-build-rework",
            timestamp: FixedTs);

        Assert.Equal("throughline-build-op-13-build-rework-scaffold-2026-05-28-143052", stem);
    }

    [Fact]
    public void Build_TicketWins_WhenBothTicketAndExtraSlugSet()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "Throughline Build",
            projectIdentifier: "TLB",
            verb: "amend",
            ticketId: "TLB-77",
            extraSlug: "ignored-slug",
            timestamp: FixedTs);

        Assert.Contains("TLB-77", stem);
        Assert.DoesNotContain("ignored-slug", stem);
    }

    [Fact]
    public void Build_DateFormat_IsSortable()
    {
        // YYYY-MM-DD-HHmmss puts older runs first in lexical sort.
        var early = SessionFileNameBuilder.Build("p", null, "v", "T-1", null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var late = SessionFileNameBuilder.Build("p", null, "v", "T-1", null,
            new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(early, late) < 0);
    }

    [Fact]
    public void Build_TwoRunsSameSecond_ProduceSameStem()
    {
        // Documents the collision window: HHmmss resolution means two invocations
        // within the same second still collide. Acceptable for human use; if it
        // ever bites, we can drop in millisecond precision.
        var a = SessionFileNameBuilder.Build("p", null, "implement", "T-1", null, FixedTs);
        var b = SessionFileNameBuilder.Build("p", null, "implement", "T-1", null, FixedTs);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("SampleProject", "sampleproject")]
    [InlineData("Sample Project", "sample-project")]
    [InlineData("Sample  Project", "sample-project")]
    [InlineData("Sample/Project!", "sample-project")]
    [InlineData("  Sample  ", "sample")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SlugifyLower_Cases(string? input, string expected)
    {
        Assert.Equal(expected, SessionFileNameBuilder.SlugifyLower(input));
    }

    [Fact]
    public void Build_BlankVerb_DefaultsToRun()
    {
        var stem = SessionFileNameBuilder.Build(
            projectName: "p", projectIdentifier: null,
            verb: "", ticketId: "T-1", extraSlug: null, timestamp: FixedTs);
        Assert.Contains("-run-", stem);
    }
}
