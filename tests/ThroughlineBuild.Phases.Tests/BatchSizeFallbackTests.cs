using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Unit tests for the batch size fallback gate (TLB-454).
/// CheckBatchSizeCaps is internal so accessible via InternalsVisibleTo.
/// </summary>
public class BatchSizeFallbackTests
{
    private static Ticket MakeTicket(string id, Size size = Size.S, string desc = "") => new Ticket(
        Id: id,
        Uuid: $"uuid-{id}",
        Title: $"Ticket {id}",
        Type: "feature",
        State: TicketState.Ready,
        Size: size,
        Risk: Risk.Low,
        DescriptionHtml: desc,
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOpts(
        int maxTickets = 8,
        int maxSizeScore = 16,
        int maxDescriptionBytes = 200_000) => new BuildOptions(
            SessionId: "test",
            WorkerName: "test-agent",
            WorkerTimeout: TimeSpan.FromMinutes(5),
            BatchMaxTickets: maxTickets,
            BatchMaxSizeScore: maxSizeScore,
            BatchMaxDescriptionBytes: maxDescriptionBytes);

    // --- All caps pass ---

    [Fact]
    public void CheckBatchSizeCaps_EmptyGroup_ReturnsNull()
    {
        var result = BatchImplementRunner.CheckBatchSizeCaps(Array.Empty<Ticket>(), MakeOpts());

        Assert.Null(result);
    }

    [Fact]
    public void CheckBatchSizeCaps_GroupWithinAllCaps_ReturnsNull()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-1", Size.S, "plan A"),
            MakeTicket("TLB-2", Size.M, "plan B"),
        };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 8, maxSizeScore: 16));

        Assert.Null(result);
    }

    [Fact]
    public void CheckBatchSizeCaps_ExactlyAtMaxTickets_ReturnsNull()
    {
        // 4 tickets, cap is 4 - should pass (boundary is inclusive)
        var tickets = Enumerable.Range(1, 4).Select(i => MakeTicket($"TLB-{i}")).ToArray();

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 4));

        Assert.Null(result);
    }

    [Fact]
    public void CheckBatchSizeCaps_ExactlyAtMaxSizeScore_ReturnsNull()
    {
        // 4 x S (score 1 each = 4 total), cap is 4
        var tickets = Enumerable.Range(1, 4).Select(i => MakeTicket($"TLB-{i}", Size.S)).ToArray();

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 10, maxSizeScore: 4));

        Assert.Null(result);
    }

    // --- Ticket count cap exceeded ---

    [Fact]
    public void CheckBatchSizeCaps_TicketCountExceedsCap_ReturnsCap()
    {
        var tickets = Enumerable.Range(1, 5).Select(i => MakeTicket($"TLB-{i}")).ToArray();

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 4));

        Assert.NotNull(result);
        Assert.Contains("max_tickets=4", result);
        Assert.Contains("actual 5", result);
    }

    [Fact]
    public void CheckBatchSizeCaps_TicketCountOneBeyondCap_ReturnsCap()
    {
        var tickets = new[] { MakeTicket("TLB-1"), MakeTicket("TLB-2") };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 1));

        Assert.NotNull(result);
        Assert.Contains("max_tickets=1", result);
    }

    // --- Size score cap exceeded ---

    [Fact]
    public void CheckBatchSizeCaps_SizeScoreExceedsCap_ReturnsCap()
    {
        // 2 L tickets: score = 4 + 4 = 8, cap is 6
        var tickets = new[]
        {
            MakeTicket("TLB-1", Size.L),
            MakeTicket("TLB-2", Size.L),
        };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 10, maxSizeScore: 6));

        Assert.NotNull(result);
        Assert.Contains("max_size_score=6", result);
        Assert.Contains("actual 8", result);
    }

    [Fact]
    public void CheckBatchSizeCaps_SizeScoreSmallMediumLarge_ComputedCorrectly()
    {
        // S=1, M=2, L=4 => total = 7, cap is 6
        var tickets = new[]
        {
            MakeTicket("TLB-1", Size.S),
            MakeTicket("TLB-2", Size.M),
            MakeTicket("TLB-3", Size.L),
        };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 10, maxSizeScore: 6));

        Assert.NotNull(result);
        Assert.Contains("max_size_score=6", result);
        Assert.Contains("actual 7", result);
    }

    // --- Description bytes cap exceeded ---

    [Fact]
    public void CheckBatchSizeCaps_DescriptionBytesExceedCap_ReturnsCap()
    {
        // Two tickets with 100 bytes each = 200 bytes total, cap is 150
        var bigDesc = new string('x', 100);
        var tickets = new[]
        {
            MakeTicket("TLB-1", Size.S, bigDesc),
            MakeTicket("TLB-2", Size.S, bigDesc),
        };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxDescriptionBytes: 150));

        Assert.NotNull(result);
        Assert.Contains("max_description_bytes=150", result);
    }

    [Fact]
    public void CheckBatchSizeCaps_DescriptionBytesExactlyAtCap_ReturnsNull()
    {
        // ASCII 'x' is 1 byte each; 100 chars = 100 bytes, cap is 100
        var desc = new string('x', 100);
        var tickets = new[] { MakeTicket("TLB-1", Size.S, desc) };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxDescriptionBytes: 100));

        Assert.Null(result);
    }

    // --- Check ordering: ticket count is checked before size score ---

    [Fact]
    public void CheckBatchSizeCaps_BothCountAndScoreExceeded_ReturnsCountCapFirst()
    {
        // 5 L tickets: count > 4, score = 20 > 10 - count cap fires first
        var tickets = Enumerable.Range(1, 5).Select(i => MakeTicket($"TLB-{i}", Size.L)).ToArray();

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxTickets: 4, maxSizeScore: 10));

        Assert.NotNull(result);
        Assert.Contains("max_tickets=4", result);
    }

    // --- Empty description contributes zero bytes ---

    [Fact]
    public void CheckBatchSizeCaps_EmptyDescriptionCountsAsZeroBytes()
    {
        // Empty description = 0 UTF-8 bytes; even a low cap should not trigger.
        var tickets = new[] { MakeTicket("TLB-1", Size.S, string.Empty) };

        var result = BatchImplementRunner.CheckBatchSizeCaps(tickets, MakeOpts(maxDescriptionBytes: 1));

        Assert.Null(result);
    }
}
