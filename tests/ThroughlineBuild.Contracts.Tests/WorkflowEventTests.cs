using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class WorkflowEventTests
{
    private static readonly DateTimeOffset Ts = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, object> EmptyData =
        new Dictionary<string, object>();

    private static WorkflowEvent MakeEvent(EventKind kind = EventKind.StateTransition) => new WorkflowEvent(
        SessionId: "sess-1",
        Timestamp: Ts,
        Kind: kind,
        TicketId: "TLB-1",
        Phase: Phase.Implement,
        Data: EmptyData);

    [Fact]
    public void WorkflowEvent_EqualWhenSameValues()
    {
        var a = MakeEvent();
        var b = MakeEvent();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void WorkflowEvent_NotEqualWhenDifferentKind()
    {
        var a = MakeEvent(EventKind.StateTransition);
        var b = MakeEvent(EventKind.LlmCall);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WorkflowEvent_WithExpression_ChangesSessionId()
    {
        var a = MakeEvent();
        var b = a with { SessionId = "sess-2" };
        Assert.Equal("sess-2", b.SessionId);
        Assert.Equal("sess-1", a.SessionId);
    }

    [Fact]
    public void WorkflowEvent_DataProperty_IsReadOnly()
    {
        var e = MakeEvent();
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(e.Data);
    }
}
