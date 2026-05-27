namespace ThroughlineBuild.EventLog;

public class EventLogOptions
{
    public required string BaseDirectory { get; init; }
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
}
