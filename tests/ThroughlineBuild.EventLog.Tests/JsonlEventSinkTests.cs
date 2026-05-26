using System.Text.Json;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

public class JsonlEventSinkTests
{
    [Fact]
    public async Task EmitAsync_SingleEvent_WritesOneJsonLine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-single" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var evt = new WorkflowEvent(
                    SessionId: "test-single",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.StateTransition,
                    TicketId: "TLB-27",
                    Phase: Phase.Plan,
                    Data: new Dictionary<string, object>()
                );

                await sink.EmitAsync(evt, CancellationToken.None);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-single.jsonl");
            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Single(lines);

            var json = JsonDocument.Parse(lines[0]);
            Assert.NotNull(json);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_MultipleEvents_WritesMultipleLines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-multiple" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var evt = new WorkflowEvent(
                        SessionId: "test-multiple",
                        Timestamp: DateTimeOffset.UtcNow,
                        Kind: EventKind.StateTransition,
                        TicketId: $"TLB-{i}",
                        Phase: Phase.Plan,
                        Data: new Dictionary<string, object> { { "index", i } }
                    );
                    await sink.EmitAsync(evt, CancellationToken.None);
                }

                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-multiple.jsonl");
            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(3, lines.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FlushAsync_EnsuresDataDurable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-flush" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var evt = new WorkflowEvent(
                    SessionId: "test-flush",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.WorkerSpawn,
                    TicketId: "TLB-27",
                    Phase: Phase.Implement,
                    Data: new Dictionary<string, object> { { "worker", "test-worker" } }
                );

                await sink.EmitAsync(evt, CancellationToken.None);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-flush.jsonl");
            Assert.True(File.Exists(filePath));

            var content = File.ReadAllText(filePath);
            Assert.NotEmpty(content);

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_Concurrent_AllEventsWritten()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-concurrent" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var tasks = new Task[10];
                for (int i = 0; i < 10; i++)
                {
                    int index = i;
                    tasks[i] = Task.Run(async () =>
                    {
                        var evt = new WorkflowEvent(
                            SessionId: "test-concurrent",
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.LlmCall,
                            TicketId: $"TLB-{index}",
                            Phase: Phase.Implement,
                            Data: new Dictionary<string, object> { { "task", index } }
                        );
                        await sink.EmitAsync(evt, CancellationToken.None);
                    });
                }

                await Task.WhenAll(tasks);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-concurrent.jsonl");
            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(10, lines.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Construction_WithoutEmit_DoesNotCreateFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var sessionId = "test-no-emit";
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = sessionId };

        try
        {
            var sink = new JsonlEventSink(options);
            await sink.DisposeAsync();

            var expectedPath = Path.Combine(tempDir, $"{sessionId}.jsonl");
            Assert.False(File.Exists(expectedPath));
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionId_DeterminesFileName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var sessionId = "my-custom-session";
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = sessionId };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var evt = new WorkflowEvent(
                    SessionId: sessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.VerifierVerdict,
                    TicketId: "TLB-27",
                    Phase: Phase.Review,
                    Data: new Dictionary<string, object>()
                );

                await sink.EmitAsync(evt, CancellationToken.None);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var expectedFilePath = Path.Combine(tempDir, $"{sessionId}.jsonl");
            Assert.True(File.Exists(expectedFilePath));

            var content = File.ReadAllText(expectedFilePath);
            Assert.NotEmpty(content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
