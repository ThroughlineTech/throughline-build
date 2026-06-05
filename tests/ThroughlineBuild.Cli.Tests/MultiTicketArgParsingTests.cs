using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for multi-ticket argument parsing (CliArgParser.ExtractTicketIds).
/// </summary>
public class MultiTicketArgParsingTests
{
    [Fact]
    public void ExtractTicketIds_SingleId_ReturnsOneTicketAndEmptyRemaining()
    {
        var args = new[] { "chain", "TLB-1" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Single(ticketIds);
        Assert.Equal("TLB-1", ticketIds[0]);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractTicketIds_MultipleIds_ReturnsAllTicketsAndEmptyRemaining()
    {
        var args = new[] { "chain", "TLB-1", "TLB-2", "TLB-3" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Equal(3, ticketIds.Count);
        Assert.Equal("TLB-1", ticketIds[0]);
        Assert.Equal("TLB-2", ticketIds[1]);
        Assert.Equal("TLB-3", ticketIds[2]);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractTicketIds_IdsAndFlags_StopsAtFirstFlag()
    {
        var args = new[] { "chain", "TLB-1", "TLB-2", "--debug" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Equal(2, ticketIds.Count);
        Assert.Equal("TLB-1", ticketIds[0]);
        Assert.Equal("TLB-2", ticketIds[1]);
        Assert.Single(remaining);
        Assert.Equal("--debug", remaining[0]);
    }

    [Fact]
    public void ExtractTicketIds_IdsAndMultipleFlags_StopsAtFirstFlagAndIncludesRest()
    {
        var args = new[] { "chain", "TLB-1", "TLB-2", "--agent", "claude-code", "--debug" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Equal(2, ticketIds.Count);
        Assert.Equal("TLB-1", ticketIds[0]);
        Assert.Equal("TLB-2", ticketIds[1]);
        Assert.Equal(3, remaining.Count);
        Assert.Equal("--agent", remaining[0]);
        Assert.Equal("claude-code", remaining[1]);
        Assert.Equal("--debug", remaining[2]);
    }

    [Fact]
    public void ExtractTicketIds_FlagsOnly_ReturnsEmptyTicketsAndAllFlags()
    {
        var args = new[] { "chain", "--debug", "--agent", "fast" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Empty(ticketIds);
        Assert.Equal(3, remaining.Count);
        Assert.Equal("--debug", remaining[0]);
        Assert.Equal("--agent", remaining[1]);
        Assert.Equal("fast", remaining[2]);
    }

    [Fact]
    public void ExtractTicketIds_JustVerb_ReturnsEmptyTicketsAndEmptyRemaining()
    {
        var args = new[] { "chain" };
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Empty(ticketIds);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractTicketIds_EmptyArgs_ReturnsEmptyTicketsAndEmptyRemaining()
    {
        var args = Array.Empty<string>();
        var (ticketIds, remaining) = CliArgParser.ExtractTicketIds(args);

        Assert.Empty(ticketIds);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractBatchImplementFlag_ValidList_ReturnsOrderedTicketsAndRemovesFlagPair()
    {
        var args = new[] { "chain", "TLB-418", "--batch-implement", "TLB-419,TLB-420,TLB-421", "--debug" };
        var (ticketIds, error, remaining) = CliArgParser.ExtractBatchImplementFlag(args);

        Assert.Null(error);
        Assert.Equal(new[] { "TLB-419", "TLB-420", "TLB-421" }, ticketIds);
        Assert.Equal(new[] { "chain", "TLB-418", "--debug" }, remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TLB-419,")]
    [InlineData(",TLB-419")]
    [InlineData("TLB-419,,TLB-420")]
    [InlineData("not-a-ticket")]
    public void ExtractBatchImplementFlag_MalformedList_ReturnsOperatorError(string list)
    {
        var args = new[] { "chain", "TLB-418", "--batch-implement", list };
        var (ticketIds, error, _) = CliArgParser.ExtractBatchImplementFlag(args);

        Assert.Null(ticketIds);
        Assert.NotNull(error);
        Assert.Contains("--batch-implement", error);
    }

    [Fact]
    public void ExtractBatchImplementFlag_MissingValue_ReturnsOperatorError()
    {
        var args = new[] { "chain", "TLB-418", "--batch-implement", "--debug" };
        var (ticketIds, error, _) = CliArgParser.ExtractBatchImplementFlag(args);

        Assert.Null(ticketIds);
        Assert.Equal("Error: --batch-implement requires a comma-separated ticket list", error);
    }

    [Fact]
    public void CliUsage_ContainsMultiTicketSyntaxForMultiTicketVerbs()
    {
        var usage = CliUsage.UsageText;
        // Check that at least one multi-ticket verb has the new syntax
        Assert.Contains("build plan <ticket-id> [ticket-id ...]", usage);
        Assert.Contains("build implement <ticket-id> [ticket-id ...]", usage);
        Assert.Contains("build ship <ticket-id> [ticket-id ...]", usage);
        Assert.Contains("build chain <ticket-id> [ticket-id ...]", usage);
    }

    [Fact]
    public void CliUsage_DescribesMultiTicketBehavior()
    {
        var usage = CliUsage.UsageText;
        // Should mention sequential dispatch and stopping at first failure
        Assert.Contains("sequentially", usage);
        Assert.Contains("first failure", usage);
    }
}
