namespace ThroughlineBuild.Contracts.Models;

public record WorkflowEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    EventKind Kind,
    string TicketId,
    Phase Phase,
    IReadOnlyDictionary<string, object> Data);

// Integer values: StateTransition=0, LlmCall=1, WorkerSpawn=2, VerifierVerdict=3, GateFailure=4, TicketWrite=5,
//                 ChainStart=6, ChainEnd=7, ReworkRound=8, TicketSubsumed=9, TargetAutoRebased=10,
//                 DispatchStart=11, DispatchEnd=12
public enum EventKind { StateTransition, LlmCall, WorkerSpawn, VerifierVerdict, GateFailure, TicketWrite, ChainStart, ChainEnd, ReworkRound, TicketSubsumed, TargetAutoRebased, DispatchStart, DispatchEnd }
