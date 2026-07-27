using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Unit tests for the batch rework routing decision (TLB-453).
/// ClassifyBatchRework is internal so accessible via InternalsVisibleTo.
/// </summary>
public class BatchReworkRoutingTests
{
    private static Ticket MakeTicket(string id) => new Ticket(
        Id: id,
        Uuid: $"uuid-{id}",
        Title: $"Ticket {id}",
        Type: "feature",
        State: TicketState.InReview,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: $"<p>plan for {id}</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static readonly IReadOnlyList<Ticket> ThreeTickets = new[]
    {
        MakeTicket("TLB-10"),
        MakeTicket("TLB-20"),
        MakeTicket("TLB-30"),
    };

    // --- ClassifyBatchRework: localized cases ---

    [Fact]
    public void ClassifyBatchRework_RationaleNamesExactlyOneTicket_ReturnsLocalized()
    {
        var rationale = "TLB-20 has a missing null check in Foo.cs.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.Localized, route);
    }

    [Fact]
    public void ClassifyBatchRework_RationaleNamesFirstTicketOnly_ReturnsLocalized()
    {
        var rationale = "TLB-10 needs the interface updated to match the spec.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.Localized, route);
    }

    [Fact]
    public void ClassifyBatchRework_RationaleNamesLastTicketOnly_ReturnsLocalized()
    {
        var rationale = "The test in TLB-30 is failing due to a bad assertion.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.Localized, route);
    }

    // --- ClassifyBatchRework: cross-ticket cases ---

    [Fact]
    public void ClassifyBatchRework_RationaleNamesNoTickets_ReturnsCrossTicket()
    {
        var rationale = "The interface between the commits is broken; the seam is wrong.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }

    [Fact]
    public void ClassifyBatchRework_RationaleNamesTwoTickets_ReturnsCrossTicket()
    {
        var rationale = "TLB-10 and TLB-20 share a broken interface contract.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }

    [Fact]
    public void ClassifyBatchRework_RationaleNamesAllThreeTickets_ReturnsCrossTicket()
    {
        var rationale = "TLB-10, TLB-20, and TLB-30 all need adjustments.";

        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }

    [Fact]
    public void ClassifyBatchRework_EmptyRationale_ReturnsCrossTicket()
    {
        var route = BatchReviewRunner.ClassifyBatchRework(ThreeTickets, string.Empty);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }

    [Fact]
    public void ClassifyBatchRework_SingleTicketGroup_MatchingRationale_ReturnsLocalized()
    {
        var singleTicket = new[] { MakeTicket("TLB-99") };
        var rationale = "TLB-99 is missing tests.";

        var route = BatchReviewRunner.ClassifyBatchRework(singleTicket, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.Localized, route);
    }

    [Fact]
    public void ClassifyBatchRework_SingleTicketGroup_NonMatchingRationale_ReturnsCrossTicket()
    {
        var singleTicket = new[] { MakeTicket("TLB-99") };
        var rationale = "The overall architecture needs a rethink.";

        var route = BatchReviewRunner.ClassifyBatchRework(singleTicket, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }

    [Fact]
    public void ClassifyBatchRework_TicketIdSubstringDoesNotCount_ExactMatch()
    {
        // "TLB-1" appears in "TLB-10" but should not match ticket TLB-1 if it's not in the list
        var tickets = new[]
        {
            MakeTicket("TLB-10"),
            MakeTicket("TLB-20"),
        };
        // Rationale contains "TLB-10" and "TLB-20" - two tickets = CrossTicket
        var rationale = "Issues found in TLB-10 and TLB-20.";

        var route = BatchReviewRunner.ClassifyBatchRework(tickets, rationale);

        Assert.Equal(BatchReviewRunner.BatchReworkRoute.CrossTicket, route);
    }
}
