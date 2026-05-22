namespace ThroughlineBuild.Contracts.Models;

public record WorkflowEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    EventKind Kind,
    string TicketId,
    Phase Phase,
    IReadOnlyDictionary<string, object> Data);

public enum EventKind { StateTransition, LlmCall, WorkerSpawn, VerifierVerdict, GateFailure, TicketWrite }
