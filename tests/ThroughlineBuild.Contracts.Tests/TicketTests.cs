using Xunit;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

public class TicketTests
{
    private static Ticket MakeTicket(string id = "TLB-1") => new Ticket(
        Id: id,
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.M,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    [Fact]
    public void Ticket_EqualWhenSameValues()
    {
        var a = MakeTicket();
        var b = MakeTicket();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Ticket_NotEqualWhenDifferentId()
    {
        var a = MakeTicket("TLB-1");
        var b = MakeTicket("TLB-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Ticket_WithExpression_ChangesState()
    {
        var a = MakeTicket();
        var b = a with { State = TicketState.Done };
        Assert.Equal(TicketState.Done, b.State);
        Assert.Equal(TicketState.Ready, a.State);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Ticket_RelationsProperty_IsReadOnly()
    {
        var t = MakeTicket();
        Assert.IsAssignableFrom<IReadOnlyList<Relation>>(t.Relations);
    }

    [Fact]
    public void Ticket_LabelsProperty_IsReadOnly()
    {
        var t = MakeTicket();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(t.Labels);
    }
}
