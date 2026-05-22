using Xunit;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

public class BriefTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyCtx =
        new Dictionary<string, string>();

    private static Brief MakeBrief(string ticketId = "TLB-1") => new Brief(
        TicketId: ticketId,
        Phase: Phase.Implement,
        Instruction: "Do the thing",
        RelevantFiles: Array.Empty<string>(),
        AllowedWrites: Array.Empty<string>(),
        Context: EmptyCtx);

    [Fact]
    public void Brief_EqualWhenSameValues()
    {
        var a = MakeBrief();
        var b = MakeBrief();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Brief_NotEqualWhenDifferentTicketId()
    {
        var a = MakeBrief("TLB-1");
        var b = MakeBrief("TLB-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Brief_WithExpression_ChangesPhase()
    {
        var a = MakeBrief();
        var b = a with { Phase = Phase.Review };
        Assert.Equal(Phase.Review, b.Phase);
        Assert.Equal(Phase.Implement, a.Phase);
    }

    [Fact]
    public void Brief_ContextProperty_IsReadOnly()
    {
        var b = MakeBrief();
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(b.Context);
    }
}
