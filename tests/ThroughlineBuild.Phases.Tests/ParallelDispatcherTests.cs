using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ParallelDispatcherTests
{
    // Build a completed ChainResult for a given ticket ID.
    private static ChainResult MakeOkResult(string id) =>
        new ChainResult(
            TicketId: id,
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.FromMilliseconds(10),
            FinalRationale: null);

    // Build a failed ChainResult for a given ticket ID.
    private static ChainResult MakeFailResult(string id) =>
        new ChainResult(
            TicketId: id,
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.StoppedAtPlan,
            TotalDuration: TimeSpan.FromMilliseconds(5),
            FinalRationale: "plan failed");

    private static ChainPhaseOptions BaseOptions =>
        new ChainPhaseOptions(TicketId: "ignored", Debug: false);

    // -------------------------------------------------------------------------
    // Fake event sink that records emitted events
    // -------------------------------------------------------------------------

    private sealed class RecordingEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Helpers to build a ParallelDispatcher with a fake chain runner
    // -------------------------------------------------------------------------

    private static (ParallelDispatcher dispatcher, RecordingEventSink sink) MakeDispatcher(
        Dictionary<string, ChainResult> results,
        int maxConcurrency = 4,
        List<string>? callOrder = null)
    {
        var sink = new RecordingEventSink();
        async Task<ChainResult> RunChain(ChainPhaseOptions opts, CancellationToken ct)
        {
            callOrder?.Add(opts.TicketId);
            if (!results.TryGetValue(opts.TicketId, out var r))
                return MakeOkResult(opts.TicketId);
            await Task.Yield(); // simulate async
            return r;
        }
        var dispatcher = new ParallelDispatcher(RunChain, sink, maxConcurrency,
            sessionIdGenerator: () => "test-session");
        return (dispatcher, sink);
    }

    // -------------------------------------------------------------------------
    // Tests: levels run in order
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IndependentTickets_AllDispatched_AllSucceed()
    {
        var ids = new[] { "A", "B", "C" };
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A"),
            ["B"] = MakeOkResult("B"),
            ["C"] = MakeOkResult("C")
        };
        var g = new TicketGraph();
        foreach (var id in ids) g.AddNode(id);

        var (dispatcher, sink) = MakeDispatcher(results);
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(3, outcome.Results.Count);
        Assert.All(outcome.Results, r => Assert.Equal(ChainOutcome.Completed, r.Outcome));
    }

    [Fact]
    public async Task LinearChain_ABlocksB_LevelOrderRespected()
    {
        // A -> B: A must complete before B is dispatched.
        var callOrder = new List<string>();
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A"),
            ["B"] = MakeOkResult("B")
        };
        var g = new TicketGraph();
        g.AddNode("A");
        g.AddNode("B");
        g.AddEdge("A", "B");

        var (dispatcher, _) = MakeDispatcher(results, callOrder: callOrder);
        var ids = new[] { "A", "B" };
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("A", callOrder[0]);
        Assert.Equal("B", callOrder[1]);
    }

    [Fact]
    public async Task FailInFirstLevel_StopsDispatch_LaterLevelsNotRun()
    {
        // A -> B: A fails; B should not be dispatched.
        var callOrder = new List<string>();
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeFailResult("A"),
            ["B"] = MakeOkResult("B")
        };
        var g = new TicketGraph();
        g.AddNode("A");
        g.AddNode("B");
        g.AddEdge("A", "B");

        var (dispatcher, _) = MakeDispatcher(results, callOrder: callOrder);
        var ids = new[] { "A", "B" };
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureReason);
        // Only A was called; B was blocked by A's failure
        Assert.Equal(new[] { "A" }, callOrder);
        // B is not in results because it was never dispatched
        Assert.Single(outcome.Results);
        Assert.Equal("A", outcome.Results[0].TicketId);
    }

    // -------------------------------------------------------------------------
    // Tests: concurrency cap
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MaxConcurrencyOne_IndependentTickets_AllSucceed_SequentialExecution()
    {
        // With maxConcurrency=1, all three independent nodes still complete.
        var ids = new[] { "X", "Y", "Z" };
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["X"] = MakeOkResult("X"),
            ["Y"] = MakeOkResult("Y"),
            ["Z"] = MakeOkResult("Z")
        };
        var g = new TicketGraph();
        foreach (var id in ids) g.AddNode(id);

        var (dispatcher, _) = MakeDispatcher(results, maxConcurrency: 1);
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.Results.Count);
    }

    // -------------------------------------------------------------------------
    // Tests: cancellation propagation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CancellationBeforeDispatch_ReturnsFailed()
    {
        var ids = new[] { "A" };
        var g = new TicketGraph();
        g.AddNode("A");
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var (dispatcher, _) = MakeDispatcher(results);
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, cts.Token);

        // Either failed because cancelled or the task never ran
        // A pre-cancelled token causes SemaphoreSlim.WaitAsync to throw immediately
        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task CycleInGraph_ReturnsFailed_WithErrorMessage()
    {
        // A -> B -> A is a cycle; dispatcher should return failure without throwing.
        var ids = new[] { "A", "B" };
        var g = new TicketGraph();
        g.AddNode("A");
        g.AddNode("B");
        g.AddEdge("A", "B");
        g.AddEdge("B", "A");

        var (dispatcher, _) = MakeDispatcher(new Dictionary<string, ChainResult>());
        var outcome = await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureReason);
        Assert.Contains("Cycle detected", outcome.FailureReason);
    }

    // -------------------------------------------------------------------------
    // Tests: event emission
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DispatchStartAndEnd_EventsEmitted()
    {
        var ids = new[] { "A" };
        var g = new TicketGraph();
        g.AddNode("A");
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A")
        };

        var (dispatcher, sink) = MakeDispatcher(results);
        await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        var kinds = sink.Events.Select(e => e.Kind).ToList();
        Assert.Contains(EventKind.DispatchStart, kinds);
        Assert.Contains(EventKind.DispatchEnd, kinds);
        // DispatchStart must come before DispatchEnd
        var startIdx = kinds.IndexOf(EventKind.DispatchStart);
        var endIdx = kinds.IndexOf(EventKind.DispatchEnd);
        Assert.True(startIdx < endIdx);
    }

    [Fact]
    public async Task DispatchStart_HasCorrectMetadata()
    {
        var ids = new[] { "A", "B" };
        var g = new TicketGraph();
        g.AddNode("A");
        g.AddNode("B");
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A"),
            ["B"] = MakeOkResult("B")
        };

        var (dispatcher, sink) = MakeDispatcher(results, maxConcurrency: 2);
        await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        var start = sink.Events.Single(e => e.Kind == EventKind.DispatchStart);
        Assert.Equal(2, Convert.ToInt32(start.Data["ticket_count"]));
        Assert.Equal(2, Convert.ToInt32(start.Data["max_concurrency"]));
    }

    [Fact]
    public async Task DispatchEnd_OutcomeOk_WhenAllSucceed()
    {
        var ids = new[] { "A" };
        var g = new TicketGraph();
        g.AddNode("A");
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeOkResult("A")
        };

        var (dispatcher, sink) = MakeDispatcher(results);
        await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        var end = sink.Events.Single(e => e.Kind == EventKind.DispatchEnd);
        Assert.Equal("ok", (string)end.Data["outcome"]);
    }

    [Fact]
    public async Task DispatchEnd_OutcomePartial_WhenAnyFail()
    {
        var ids = new[] { "A" };
        var g = new TicketGraph();
        g.AddNode("A");
        var results = new Dictionary<string, ChainResult>(StringComparer.Ordinal)
        {
            ["A"] = MakeFailResult("A")
        };

        var (dispatcher, sink) = MakeDispatcher(results);
        await dispatcher.RunAsync(ids, g, BaseOptions, CancellationToken.None);

        var end = sink.Events.Single(e => e.Kind == EventKind.DispatchEnd);
        Assert.Equal("partial", (string)end.Data["outcome"]);
    }
}
