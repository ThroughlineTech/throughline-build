using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Owns event construction and best-effort informational ticket writes for one chain session.
/// </summary>
public sealed class ChainEventEmitter
{
    private readonly IEventSink _events;
    private readonly ITicketing _ticketing;
    private readonly string _chainSessionId;
    private readonly TextWriter _diagnostics;

    public ChainEventEmitter(
        IEventSink events,
        ITicketing ticketing,
        string chainSessionId,
        TextWriter? diagnostics = null)
    {
        _events = events;
        _ticketing = ticketing;
        _chainSessionId = chainSessionId;
        _diagnostics = diagnostics ?? TextWriter.Null;
    }

    public Task EmitAsync(
        EventKind kind,
        string ticketId,
        Phase phase,
        IReadOnlyDictionary<string, object> data,
        CancellationToken ct) =>
        _events.EmitAsync(new WorkflowEvent(
            SessionId: _chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: kind,
            TicketId: ticketId,
            Phase: phase,
            Data: data), ct);

    // Emits a pre-run START notice through the OnStep stream so the operator sees a
    // phase has begun, not just its completion line. Start markers are progress-only:
    // they are never added to the steps list, so the returned ChainResult is unchanged.
    public void EmitPhaseStart(
        ChainPhaseOptions options,
        string phaseName,
        int reworkRoundNumber,
        string sessionId)
    {
        options.OnStep?.Invoke(options.TicketId, new ChainStep(
            PhaseName: phaseName,
            ReworkRoundNumber: reworkRoundNumber,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: null,
            Duration: TimeSpan.Zero,
            PhaseSessionId: sessionId,
            IsStart: true));
    }

    public Task EmitChainEndAsync(
        ChainResult result,
        string ticketId,
        CancellationToken ct)
    {
        var reworkRounds = result.Steps.Count(
            step => step.PhaseName == "implement" && step.ReworkRoundNumber >= 1);
        var data = new Dictionary<string, object>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["phases_run"] = result.Steps.Count,
            ["rework_rounds"] = reworkRounds,
            ["total_duration_ms"] = (long)result.TotalDuration.TotalMilliseconds
        };
        var preview = ImplementReviewLoop.RationalePreview(
            result.FinalRationale);
        if (preview != null)
            data["final_rationale_preview"] = preview;

        return EmitAsync(EventKind.ChainEnd, ticketId, Phase.Chain, data, ct);
    }

    /// <summary>
    /// Runs a non-state-bearing ticketing write without allowing its failure to abort the chain.
    /// </summary>
    public Task BestEffortTicketWriteAsync(
        string ticketId,
        string operation,
        Func<ITicketing, Task> write,
        CancellationToken ct) =>
        TicketingWritePolicy.BestEffortAsync(
            _events,
            _chainSessionId,
            ticketId,
            Phase.Chain,
            operation,
            () => write(_ticketing),
            _diagnostics,
            ct);

    public Task EmitCostLedgerAsync(
        string ticketId,
        long gateWallMs,
        int gateAttributableReworkRounds,
        long gateAttributableReworkInputTokens,
        long gateAttributableReworkOutputTokens,
        bool gateAttributableReworkTokensTracked,
        CancellationToken ct,
        int falseFails = 0)
    {
        var data = new Dictionary<string, object>
        {
            ["gate_wall_ms"] = gateWallMs,
            ["gate_attributable_rework_rounds"] = gateAttributableReworkRounds,
            ["cascade_caught"] = 0,
            ["false_fails"] = falseFails
        };
        if (gateAttributableReworkRounds > 0 && gateAttributableReworkTokensTracked)
        {
            data["gate_attributable_rework_input_tokens"] = gateAttributableReworkInputTokens;
            data["gate_attributable_rework_output_tokens"] = gateAttributableReworkOutputTokens;
        }
        else if (gateAttributableReworkRounds > 0)
        {
            data["gate_attributable_rework_tokens_available"] = false;
        }
        return EmitAsync(EventKind.CostLedger, ticketId, Phase.Gate, data, ct);
    }

    public Task EmitChainGateFailureAsync(
        string ticketId,
        string kind,
        Dictionary<string, object> data,
        CancellationToken ct)
    {
        data["kind"] = kind;
        return EmitAsync(EventKind.GateFailure, ticketId, Phase.Chain, data, ct);
    }

    public static void RecordBatchTicketingUnavailable(
        List<ChainResult> results,
        IReadOnlyList<Ticket> batchTickets,
        string failedTicketId,
        Exception exception)
    {
        var batchIds = new HashSet<string>(batchTickets.Select(ticket => ticket.Id), StringComparer.Ordinal);
        results.RemoveAll(result => batchIds.Contains(result.TicketId));
        results.Add(new ChainResult(
            failedTicketId,
            Array.Empty<ChainStep>(),
            ChainOutcome.TicketingUnavailable,
            TimeSpan.Zero,
            exception.Message));
        results.AddRange(batchTickets
            .Where(ticket => !string.Equals(ticket.Id, failedTicketId, StringComparison.Ordinal))
            .Select(ticket => new ChainResult(
                ticket.Id,
                Array.Empty<ChainStep>(),
                ChainOutcome.Skipped,
                TimeSpan.Zero,
                null,
                SkipReason:
                    $"ticketing backend unreachable while updating {failedTicketId}; restore connectivity and re-run")));
    }

}
