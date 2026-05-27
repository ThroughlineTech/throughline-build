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

public class JsonlEventSinkSessionContextTests
{
    private static WorkflowEvent MakeEvent(string sessionId = "test-session") => new WorkflowEvent(
        SessionId: sessionId,
        Timestamp: DateTimeOffset.UtcNow,
        Kind: EventKind.StateTransition,
        TicketId: "TLB-147",
        Phase: Phase.Plan,
        Data: new Dictionary<string, object> { { "from", "Backlog" }, { "to", "Ready" } });

    [Fact]
    public async Task EmitAsync_WithSessionContext_WritesAllFourSessionFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-ctx" };
        var session = new SessionContext("proj-uuid-123", "My Project", "my-workspace", "1.2.3");

        try
        {
            await using var sink = new JsonlEventSink(options, session);
            await sink.EmitAsync(MakeEvent("test-ctx"), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);

            var line = File.ReadAllLines(Path.Combine(tempDir, "test-ctx.jsonl"))[0];
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.Equal("proj-uuid-123", root.GetProperty("project_id").GetString());
            Assert.Equal("My Project", root.GetProperty("project_name").GetString());
            Assert.Equal("my-workspace", root.GetProperty("workspace_slug").GetString());
            Assert.Equal("1.2.3", root.GetProperty("build_version").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_WithSessionContext_PreservesExistingPascalCaseFieldNames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-pascal" };
        var session = new SessionContext("pid", "Name", "slug", "1.0.0");

        try
        {
            await using var sink = new JsonlEventSink(options, session);
            await sink.EmitAsync(MakeEvent("test-pascal"), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);

            var line = File.ReadAllLines(Path.Combine(tempDir, "test-pascal.jsonl"))[0];
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Original 6 fields must still be PascalCase
            Assert.True(root.TryGetProperty("SessionId", out _), "SessionId must be PascalCase");
            Assert.True(root.TryGetProperty("Timestamp", out _), "Timestamp must be PascalCase");
            Assert.True(root.TryGetProperty("Kind", out _), "Kind must be PascalCase");
            Assert.True(root.TryGetProperty("TicketId", out _), "TicketId must be PascalCase");
            Assert.True(root.TryGetProperty("Phase", out _), "Phase must be PascalCase");
            Assert.True(root.TryGetProperty("Data", out _), "Data must be PascalCase");
            // snake_case variants must NOT be present for the original fields
            Assert.False(root.TryGetProperty("session_id", out _), "session_id must not appear");
            Assert.False(root.TryGetProperty("ticket_id", out _), "ticket_id must not appear");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_WithoutSessionContext_DoesNotWriteSessionFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-no-ctx" };

        try
        {
            // No session context - null default
            await using var sink = new JsonlEventSink(options);
            await sink.EmitAsync(MakeEvent("test-no-ctx"), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);

            var line = File.ReadAllLines(Path.Combine(tempDir, "test-no-ctx.jsonl"))[0];
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.False(root.TryGetProperty("project_id", out _), "project_id must be absent when no SessionContext");
            Assert.False(root.TryGetProperty("project_name", out _), "project_name must be absent when no SessionContext");
            Assert.False(root.TryGetProperty("workspace_slug", out _), "workspace_slug must be absent when no SessionContext");
            Assert.False(root.TryGetProperty("build_version", out _), "build_version must be absent when no SessionContext");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_SessionContextWithNullProjectName_OmitsProjectName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-null-name" };
        // ProjectName is null (e.g. config key absent)
        var session = new SessionContext("proj-id", null, "my-slug", "2.0.0");

        try
        {
            await using var sink = new JsonlEventSink(options, session);
            await sink.EmitAsync(MakeEvent("test-null-name"), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);

            var line = File.ReadAllLines(Path.Combine(tempDir, "test-null-name.jsonl"))[0];
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("project_id", out _));
            Assert.False(root.TryGetProperty("project_name", out _), "project_name must be absent when null");
            Assert.True(root.TryGetProperty("workspace_slug", out _));
            Assert.True(root.TryGetProperty("build_version", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
