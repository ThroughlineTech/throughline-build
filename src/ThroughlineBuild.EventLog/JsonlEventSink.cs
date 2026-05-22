using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

public sealed class JsonlEventSink : IEventSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly FileStream _stream;
    private static readonly byte[] Newline = Encoding.UTF8.GetBytes("\n");

    public JsonlEventSink(EventLogOptions options)
    {
        var dir = options.BaseDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{options.SessionId}.jsonl");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
    }

    public async Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(ev, options);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _lock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.WriteAsync(Newline, ct);
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
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _lock.Dispose();
    }
}
