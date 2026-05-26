using System.Collections.Generic;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

public class RecordingEventSinkTests
{
    [Fact]
    public async Task EmitAsync_RecordsEventsAndForwardsToInnerSink()
    {
        var inner = new MemorySink();
        await using var rec = new RecordingEventSink(inner);

        var ev1 = MakeEvent(Phase.Plan, EventKind.WorkerSpawn);
        var ev2 = MakeEvent(Phase.Plan, EventKind.TicketWrite);
        await rec.EmitAsync(ev1, CancellationToken.None);
        await rec.EmitAsync(ev2, CancellationToken.None);

        var snapshot = rec.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(EventKind.WorkerSpawn, snapshot[0].Kind);
        Assert.Equal(EventKind.TicketWrite, snapshot[1].Kind);
        Assert.Equal(2, inner.Received.Count);
    }

    [Fact]
    public async Task Snapshot_ReturnsDefensiveCopy()
    {
        var inner = new MemorySink();
        var rec = new RecordingEventSink(inner);

        var snap1 = rec.Snapshot();
        Assert.Empty(snap1);

        await rec.EmitAsync(MakeEvent(Phase.Plan, EventKind.WorkerSpawn), CancellationToken.None);
        Assert.Empty(snap1); // the prior snapshot must not see new events
        Assert.Single(rec.Snapshot());
    }

    private static WorkflowEvent MakeEvent(Phase phase, EventKind kind) =>
        new("sess", DateTimeOffset.UtcNow, kind, "TLB-X", phase, new Dictionary<string, object>());

    private sealed class MemorySink : IEventSink
    {
        public List<WorkflowEvent> Received { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Received.Add(ev);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
