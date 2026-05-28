namespace ThroughlineBuild.Contracts;

/// Converts a single raw NDJSON stream-event line into a short human-readable
/// digest string, or returns null when the event is uninteresting.
/// Implementations are agent-specific. A null IWorkerProgressDigester on
/// IWorkerAgent means the agent does not support live progress digests.
/// Implementations must be best-effort: FormatLine must not throw.
public interface IWorkerProgressDigester
{
    string? FormatLine(string rawNdjsonLine);
}
