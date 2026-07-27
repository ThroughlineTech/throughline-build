using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public sealed class ChainEventEmitterTests
{
    private const string SessionId = "chain-session";
    private const string TicketId = "TLB-571";

    [Fact]
    public async Task EmitAsync_ConstructsEventWithBoundSessionAndPayload()
    {
        var sink = new RecordingEventSink();
        var emitter = new ChainEventEmitter(sink, new FakeTicketing(), SessionId);
        var data = new Dictionary<string, object> { ["key"] = "value" };
        var before = DateTimeOffset.UtcNow;

        await emitter.EmitAsync(
            EventKind.ChainStart,
            TicketId,
            Phase.Chain,
            data,
            CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        var emitted = Assert.Single(sink.Events);
        Assert.Equal(SessionId, emitted.SessionId);
        Assert.InRange(emitted.Timestamp, before, after);
        Assert.Equal(EventKind.ChainStart, emitted.Kind);
        Assert.Equal(TicketId, emitted.TicketId);
        Assert.Equal(Phase.Chain, emitted.Phase);
        Assert.Same(data, emitted.Data);
    }

    [Fact]
    public void EmitPhaseStart_InvokesStepCallbackWithoutRecordingResultStep()
    {
        var emitter = new ChainEventEmitter(
            new RecordingEventSink(),
            new FakeTicketing(),
            SessionId);
        ChainStep? observed = null;
        var options = new ChainPhaseOptions(
            TicketId,
            Debug: false,
            OnStep: (_, step) => observed = step);

        emitter.EmitPhaseStart(options, "implement", 2, "phase-session");

        Assert.NotNull(observed);
        Assert.True(observed.IsStart);
        Assert.Equal("implement", observed.PhaseName);
        Assert.Equal(2, observed.ReworkRoundNumber);
        Assert.Equal("phase-session", observed.PhaseSessionId);
        Assert.Equal(Status.Ok, observed.Status);
    }

    [Fact]
    public async Task EmitChainEndAsync_PreservesDataKeysAndRationaleLimit()
    {
        var sink = new RecordingEventSink();
        var emitter = new ChainEventEmitter(sink, new FakeTicketing(), SessionId);
        var rationale = new string('r', 201);
        var steps = new[]
        {
            MakeStep("implement", 0),
            MakeStep("implement", 1),
            MakeStep("review", 1)
        };
        var result = new ChainResult(
            TicketId,
            steps,
            ChainOutcome.StoppedAtReview,
            TimeSpan.FromMilliseconds(1234),
            rationale);

        await emitter.EmitChainEndAsync(result, TicketId, CancellationToken.None);

        var emitted = Assert.Single(sink.Events);
        Assert.Equal(EventKind.ChainEnd, emitted.Kind);
        Assert.Equal(Phase.Chain, emitted.Phase);
        Assert.Equal(
            new[]
            {
                "final_rationale_preview",
                "outcome",
                "phases_run",
                "rework_rounds",
                "total_duration_ms"
            },
            emitted.Data.Keys.OrderBy(key => key));
        Assert.Equal(ChainOutcome.StoppedAtReview.ToString(), emitted.Data["outcome"]);
        Assert.Equal(3, emitted.Data["phases_run"]);
        Assert.Equal(1, emitted.Data["rework_rounds"]);
        Assert.Equal(1234L, emitted.Data["total_duration_ms"]);
        Assert.Equal(200, Assert.IsType<string>(emitted.Data["final_rationale_preview"]).Length);
    }

    [Fact]
    public async Task BestEffortTicketWriteAsync_ThrowingWriteIsSwallowedAndRecorded()
    {
        var sink = new RecordingEventSink();
        var ticketing = new FakeTicketing { CommentException = new InvalidOperationException("backend down") };
        var emitter = new ChainEventEmitter(sink, ticketing, SessionId);

        await emitter.BestEffortTicketWriteAsync(
            TicketId,
            "test_comment",
            backend => backend.CreateCommentAsync(TicketId, "<p>test</p>", CancellationToken.None),
            CancellationToken.None);

        var emitted = Assert.Single(sink.Events);
        Assert.Equal(SessionId, emitted.SessionId);
        Assert.Equal(EventKind.TicketWrite, emitted.Kind);
        Assert.Equal(TicketId, emitted.TicketId);
        Assert.Equal(Phase.Chain, emitted.Phase);
        Assert.Equal("ticketing_write_failed", emitted.Data["action"]);
        Assert.Equal("test_comment", emitted.Data["operation"]);
        Assert.Equal("backend down", emitted.Data["error"]);
    }

    [Fact]
    public async Task CostLedgerAndChainGateFailure_PreserveKindsPhasesAndData()
    {
        var sink = new RecordingEventSink();
        var emitter = new ChainEventEmitter(sink, new FakeTicketing(), SessionId);

        await emitter.EmitCostLedgerAsync(
            TicketId,
            gateWallMs: 450,
            gateAttributableReworkRounds: 1,
            gateAttributableReworkInputTokens: 1200,
            gateAttributableReworkOutputTokens: 300,
            gateAttributableReworkTokensTracked: true,
            CancellationToken.None,
            falseFails: 2);
        var gateData = new Dictionary<string, object> { ["detail"] = "failed" };
        await emitter.EmitChainGateFailureAsync(
            TicketId,
            "chain_failure",
            gateData,
            CancellationToken.None);

        Assert.Collection(
            sink.Events,
            ledger =>
            {
                Assert.Equal(EventKind.CostLedger, ledger.Kind);
                Assert.Equal(Phase.Gate, ledger.Phase);
                Assert.Equal(450L, ledger.Data["gate_wall_ms"]);
                Assert.Equal(1, ledger.Data["gate_attributable_rework_rounds"]);
                Assert.Equal(1200L, ledger.Data["gate_attributable_rework_input_tokens"]);
                Assert.Equal(300L, ledger.Data["gate_attributable_rework_output_tokens"]);
                Assert.Equal(0, ledger.Data["cascade_caught"]);
                Assert.Equal(2, ledger.Data["false_fails"]);
            },
            gate =>
            {
                Assert.Equal(EventKind.GateFailure, gate.Kind);
                Assert.Equal(Phase.Chain, gate.Phase);
                Assert.Equal("chain_failure", gate.Data["kind"]);
                Assert.Equal("failed", gate.Data["detail"]);
            });
    }

    [Fact]
    public void RecordBatchTicketingUnavailable_ReplacesBatchResultsAndKeepsUnrelatedResults()
    {
        var batchTickets = new[] { MakeTicket("TLB-1"), MakeTicket("TLB-2") };
        var unrelated = new ChainResult(
            "TLB-9",
            Array.Empty<ChainStep>(),
            ChainOutcome.Completed,
            TimeSpan.Zero,
            null);
        var results = new List<ChainResult>
        {
            unrelated,
            new("TLB-1", Array.Empty<ChainStep>(), ChainOutcome.Completed, TimeSpan.Zero, null),
            new("TLB-2", Array.Empty<ChainStep>(), ChainOutcome.Completed, TimeSpan.Zero, null)
        };

        ChainEventEmitter.RecordBatchTicketingUnavailable(
            results,
            batchTickets,
            "TLB-1",
            new InvalidOperationException("ticket backend unavailable"));

        Assert.Equal(3, results.Count);
        Assert.Contains(unrelated, results);
        var failed = Assert.Single(results, result => result.TicketId == "TLB-1");
        Assert.Equal(ChainOutcome.TicketingUnavailable, failed.Outcome);
        Assert.Equal("ticket backend unavailable", failed.FinalRationale);
        var skipped = Assert.Single(results, result => result.TicketId == "TLB-2");
        Assert.Equal(ChainOutcome.Skipped, skipped.Outcome);
        Assert.Contains("while updating TLB-1", skipped.SkipReason);
    }

    private static ChainStep MakeStep(string phaseName, int round) =>
        new(
            PhaseName: phaseName,
            ReworkRoundNumber: round,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: null,
            Duration: TimeSpan.Zero,
            PhaseSessionId: "phase-session");

    private static Ticket MakeTicket(string id) =>
        new(
            id,
            $"uuid-{id}",
            $"Ticket {id}",
            "feature",
            TicketState.InReview,
            Size.S,
            Risk.Low,
            "<p>description</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);

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

    private sealed class FakeTicketing : ITicketing
    {
        public Exception? CommentException { get; init; }

        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            if (CommentException is not null)
                throw CommentException;
            return Task.FromResult("comment-id");
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ApplyLabelsAsync(
            string id,
            IEnumerable<string> labels,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddRelationAsync(
            string blockedId,
            string blockerId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(
            string id,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetParentAsync(
            string childUuid,
            string parentUuid,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> QueryAsync(
            TicketQuery query,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionLifecycleAsync(
            string id,
            LifecycleTransition transition,
            string? reason,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid,
            IReadOnlyList<ChildTicketSpec> children,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
