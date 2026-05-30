namespace ThroughlineBuild.Plane;

/// <summary>
/// A hard rate gate: admits at most <c>maxPerWindow</c> calls per rolling
/// <c>window</c>. When the budget is spent, <see cref="AcquireAsync"/> blocks
/// (awaits) until the oldest in-window call ages out, then admits the caller.
///
/// Every Plane HTTP request routes through one shared instance so a single
/// process stays under its configured budget. Note this gate is per-process: it
/// cannot coordinate across concurrent build instances sharing one API token, so
/// the budget is set below Plane's global 60/min limit and the client's retry
/// pipeline still has to handle a 429 originating from another process.
/// The clock and the wait are injectable so the admission logic is testable
/// without real time passing.
/// </summary>
public sealed class RequestThrottle
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Timestamps of admitted calls still inside the window, oldest-first.
    private readonly Queue<DateTimeOffset> _admitted = new();
    // Serializes admission decisions across concurrent callers.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RequestThrottle(
        int maxPerWindow,
        TimeSpan window,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (maxPerWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerWindow));
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _maxPerWindow = maxPerWindow;
        _window = window;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Blocks until a send slot is available, then records this send and returns.
    /// Callers should invoke this immediately before issuing an HTTP request.
    /// </summary>
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _now();

                // Drop timestamps that have aged out of the rolling window.
                while (_admitted.Count > 0 && now - _admitted.Peek() >= _window)
                    _admitted.Dequeue();

                if (_admitted.Count < _maxPerWindow)
                {
                    _admitted.Enqueue(now);
                    return;
                }

                // At budget: hard-wait until the oldest call leaves the window.
                var waitFor = _window - (now - _admitted.Peek());
                if (waitFor > TimeSpan.Zero)
                    await _delay(waitFor, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
