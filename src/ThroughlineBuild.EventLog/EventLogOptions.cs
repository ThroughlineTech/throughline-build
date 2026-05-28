namespace ThroughlineBuild.EventLog;

public class EventLogOptions
{
    public required string BaseDirectory { get; init; }
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    // Optional human-readable filename stem (no extension). When set, the sink
    // writes to "{FileNameStem}.jsonl" instead of "{SessionId}.jsonl". The
    // SessionId GUID still appears inside each event record as the correlation
    // key (see EventLineDto), so log analysis tooling that keys off SessionId
    // is unaffected.
    public string? FileNameStem { get; init; }
}
