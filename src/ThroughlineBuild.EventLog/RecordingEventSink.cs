using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

// Wraps an inner IEventSink, mirroring every emitted WorkflowEvent into an in-memory
// list so the orchestrator can build deterministic phase-completion summaries without
// re-parsing the JSONL file mid-run (which would race against the still-open writer).
public sealed class RecordingEventSink : IEventSink, IAsyncDisposable
{
    private readonly IEventSink _inner;
    private readonly List<WorkflowEvent> _events = new();
    private readonly object _gate = new();

    public RecordingEventSink(IEventSink inner)
    {
        _inner = inner;
    }

    public async Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
    {
        lock (_gate)
        {
            _events.Add(ev);
        }
        await _inner.EmitAsync(ev, ct).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    // Returns a defensive copy so callers can iterate without locking.
    public IReadOnlyList<WorkflowEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable iad)
            await iad.DisposeAsync().ConfigureAwait(false);
        else if (_inner is IDisposable id)
            id.Dispose();
    }
}
