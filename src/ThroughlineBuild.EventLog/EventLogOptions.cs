namespace ThroughlineBuild.EventLog;

public class EventLogOptions
{
    public string BaseDirectory { get; init; } = ".build/events";
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
}
