using System.Text.Json;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ClaudeStopHookBridgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"lattice hook tests \u03B1 {Guid.NewGuid():N}");

    [Fact]
    public async Task WriteAndRead_RoundTripsVersionedRecordInUnicodePath()
    {
        var run = CreateRun("run-1");
        var store = new ClaudeCompletionStore();

        var written = await store.WriteAsync(run, Payload());
        var read = store.Read(run);

        Assert.Equal(ClaudeCompletionStore.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal(run.RunId, read.RunId);
        Assert.Equal("session-1", read.ClaudeSessionId);
        Assert.Equal(written, read);
        Assert.Single(Directory.GetFiles(run.Path));
    }

    [Fact]
    public async Task DuplicateIdenticalInvocation_ReturnsOriginalRecord()
    {
        var run = CreateRun();
        var store = new ClaudeCompletionStore();
        var first = await store.WriteAsync(run, Payload());

        var second = await store.WriteAsync(run, Payload());

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DuplicateConflictingInvocation_FailsWithoutReplacingRecord()
    {
        var run = CreateRun();
        var store = new ClaudeCompletionStore();
        var first = await store.WriteAsync(run, Payload());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteAsync(run, Payload() with { LastAssistantMessage = "different" }));

        Assert.Equal(first, store.Read(run));
    }

    [Fact]
    public async Task StaleTempFile_DoesNotAffectCompletion()
    {
        var run = CreateRun();
        await File.WriteAllTextAsync(Path.Combine(run.Path, ".completion.crashed.tmp"), "{");

        var record = await new ClaudeCompletionStore().WriteAsync(run, Payload());

        Assert.Equal(run.RunId, record.RunId);
        Assert.True(File.Exists(run.CompletionPath));
    }

    [Fact]
    public async Task WaitAsync_ObservesCancellation()
    {
        var run = CreateRun();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClaudeCompletionStore().WaitAsync(run, cts.Token));
    }

    [Fact]
    public void Open_RejectsMissingFilePathAndRunIdMismatch()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "not-directory");
        File.WriteAllText(file, "x");

        Assert.Throws<DirectoryNotFoundException>(() => ClaudeRunDirectory.Open(file, "not-directory"));
        var directory = Path.Combine(root, "actual");
        Directory.CreateDirectory(directory);
        Assert.Throws<ArgumentException>(() => ClaudeRunDirectory.Open(directory, "expected"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"session_id\":\"only-one-field\"}")]
    public async Task Bridge_InvalidPayload_DoesNotCreateTrustedRecord(string json)
    {
        var run = CreateRun();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await ClaudeStopHookBridge.RunAsync(
            run.Path, run.RunId, new StringReader(json), stdout, stderr);

        Assert.Equal((int)ClaudeStopHookExitCode.InvalidPayload, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.False(File.Exists(run.CompletionPath));
    }

    [Fact]
    public async Task Bridge_SuccessEmitsOnlyProtocolSafeJson()
    {
        var run = CreateRun();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var json = JsonSerializer.Serialize(Payload(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        var exit = await ClaudeStopHookBridge.RunAsync(
            run.Path, run.RunId, new StringReader(json), stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(run.CompletionPath));
    }

    [Fact]
    public void Read_RejectsCompletionForDifferentRun()
    {
        var run = CreateRun();
        File.WriteAllText(run.CompletionPath, """
            {"schema_version":1,"run_id":"other","claude_session_id":"s","cwd":"c","transcript_path":"t","last_assistant_message":"m","stop_hook_active":false,"completed_at_utc":"2026-06-12T00:00:00Z"}
            """);

        Assert.Throws<InvalidDataException>(() => new ClaudeCompletionStore().Read(run));
    }

    [Theory]
    [InlineData("C:\\Program Files\\Throughline's Build\\build.exe", "'C:/Program Files/Throughline'\"'\"'s Build/build.exe'")]
    [InlineData("/opt/throughline build/build", "'/opt/throughline build/build'")]
    public void SettingsBuilder_QuotesExecutableForDemonstratedShellHook(string executable, string expectedCommandPrefix)
    {
        var json = ClaudeHookSettingsBuilder.Build(executable, "/tmp/run space/id", "id");
        using var document = JsonDocument.Parse(json);

        var hook = document.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];
        Assert.StartsWith(expectedCommandPrefix, hook.GetProperty("command").GetString());
        Assert.Equal("command", hook.GetProperty("type").GetString());
        Assert.Equal(30, hook.GetProperty("timeout").GetInt32());
    }

    private ClaudeRunDirectory CreateRun(string runId = "run-1")
    {
        var path = Path.Combine(root, runId);
        Directory.CreateDirectory(path);
        return ClaudeRunDirectory.Open(path, runId);
    }

    private static ClaudeStopHookPayload Payload() =>
        new("session-1", "C:/repo", "C:/transcript.jsonl", "done", false);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
