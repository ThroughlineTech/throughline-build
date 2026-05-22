namespace ThroughlineBuild.Contracts;

using ThroughlineBuild.Contracts.Models;

public interface IEventSink
{
    Task EmitAsync(WorkflowEvent ev, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
