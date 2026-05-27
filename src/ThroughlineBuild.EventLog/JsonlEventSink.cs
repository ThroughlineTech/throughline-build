using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

public sealed class JsonlEventSink : IEventSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FileStream? _stream;
    private bool _opened;
    private readonly EventLogOptions _options;
    private readonly SessionContext? _session;
    private static readonly byte[] Newline = Encoding.UTF8.GetBytes("\n");

    public JsonlEventSink(EventLogOptions options, SessionContext? session = null)
    {
        if (string.IsNullOrEmpty(options.BaseDirectory))
            throw new ArgumentException("BaseDirectory must not be null or empty", nameof(options));
        if (!Path.IsPathRooted(options.BaseDirectory))
            throw new ArgumentException("BaseDirectory must be an absolute path", nameof(options));
        _options = options;
        _session = session;
    }

    // Caller must already hold _lock before calling this method.
    private void EnsureOpened()
    {
        if (_opened)
            return;
        var dir = _options.BaseDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{_options.SessionId}.jsonl");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        _opened = true;
    }

    public async Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
    {
        var dto = new EventLineDto
        {
            SessionId = ev.SessionId,
            Timestamp = ev.Timestamp,
            Kind = ev.Kind,
            TicketId = ev.TicketId,
            Phase = ev.Phase,
            Data = ev.Data,
            ProjectId = _session?.ProjectId,
            ProjectName = _session?.ProjectName,
            WorkspaceSlug = _session?.WorkspaceSlug,
            BuildVersion = _session?.BuildVersion
        };
        var json = JsonSerializer.Serialize(dto, typeof(EventLineDto), EventLogJsonContext.Default);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _lock.WaitAsync(ct);
        try
        {
            EnsureOpened();
            await _stream!.WriteAsync(bytes, ct);
            await _stream!.WriteAsync(Newline, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_opened)
                return;
            await _stream!.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_opened && _stream is not null)
            await _stream.DisposeAsync();
        _lock.Dispose();
    }
}
