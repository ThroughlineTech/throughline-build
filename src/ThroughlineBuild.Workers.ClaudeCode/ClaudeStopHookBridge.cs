using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

public enum ClaudeStopHookExitCode
{
    Success = 0,
    InvalidArguments = 2,
    InvalidPayload = 3,
    ConflictingCompletion = 4,
    IoFailure = 5
}

public sealed record ClaudeStopHookPayload(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("transcript_path")] string? TranscriptPath,
    [property: JsonPropertyName("last_assistant_message")] string? LastAssistantMessage,
    [property: JsonPropertyName("stop_hook_active")] bool? StopHookActive);

public sealed record ClaudeCompletionRecord(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("claude_session_id")] string ClaudeSessionId,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("transcript_path")] string TranscriptPath,
    [property: JsonPropertyName("last_assistant_message")] string LastAssistantMessage,
    [property: JsonPropertyName("stop_hook_active")] bool StopHookActive,
    [property: JsonPropertyName("completed_at_utc")] DateTimeOffset CompletedAtUtc);

public sealed class ClaudeRunDirectory
{
    public const string CompletionFileName = "completion.json";

    private ClaudeRunDirectory(string path, string runId)
    {
        Path = path;
        RunId = runId;
    }

    public string Path { get; }
    public string RunId { get; }
    public string CompletionPath => System.IO.Path.Combine(Path, CompletionFileName);

    public static ClaudeRunDirectory Open(string runDirectory, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Run id must contain only ASCII letters, digits, '.', '_', or '-'.", nameof(runId));
        if (string.IsNullOrWhiteSpace(runDirectory) || !System.IO.Path.IsPathFullyQualified(runDirectory))
            throw new ArgumentException("Run directory must be an absolute path.", nameof(runDirectory));

        var fullPath = System.IO.Path.GetFullPath(runDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Run directory does not exist or is not a directory: {fullPath}");
        if (!string.Equals(System.IO.Path.GetFileName(fullPath), runId, StringComparison.Ordinal))
            throw new ArgumentException("Run id does not match the run directory name.", nameof(runId));

        return new ClaudeRunDirectory(fullPath, runId);
    }
}

public sealed class ClaudeCompletionStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public async Task<ClaudeCompletionRecord> WriteAsync(
        ClaudeRunDirectory run,
        ClaudeStopHookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidatePayload(payload);
        var proposed = new ClaudeCompletionRecord(
            CurrentSchemaVersion,
            run.RunId,
            payload.SessionId!,
            payload.Cwd!,
            payload.TranscriptPath!,
            payload.LastAssistantMessage!,
            payload.StopHookActive!.Value,
            DateTimeOffset.UtcNow);

        if (File.Exists(run.CompletionPath))
            return ResolveDuplicate(run, proposed);

        var tempPath = System.IO.Path.Combine(run.Path, $".completion.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, proposed, ClaudeHookJsonContext.Default.ClaudeCompletionRecord, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, run.CompletionPath, overwrite: false);
                return proposed;
            }
            catch (IOException) when (File.Exists(run.CompletionPath))
            {
                return ResolveDuplicate(run, proposed);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public ClaudeCompletionRecord Read(ClaudeRunDirectory run)
    {
        using var stream = new FileStream(run.CompletionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var record = JsonSerializer.Deserialize(stream, ClaudeHookJsonContext.Default.ClaudeCompletionRecord)
            ?? throw new InvalidDataException("Completion record is empty.");
        ValidateRecord(run, record);
        return record;
    }

    public async Task<ClaudeCompletionRecord> WaitAsync(ClaudeRunDirectory run, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(run.CompletionPath)) return Read(run);
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private ClaudeCompletionRecord ResolveDuplicate(ClaudeRunDirectory run, ClaudeCompletionRecord proposed)
    {
        var existing = Read(run);
        if (!SameEvent(existing, proposed))
            throw new InvalidOperationException("A conflicting completion record already exists for this run.");
        return existing;
    }

    private static bool SameEvent(ClaudeCompletionRecord left, ClaudeCompletionRecord right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.RunId == right.RunId &&
        left.ClaudeSessionId == right.ClaudeSessionId &&
        left.Cwd == right.Cwd &&
        left.TranscriptPath == right.TranscriptPath &&
        left.LastAssistantMessage == right.LastAssistantMessage &&
        left.StopHookActive == right.StopHookActive;

    private static void ValidatePayload(ClaudeStopHookPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.SessionId) ||
            string.IsNullOrWhiteSpace(payload.Cwd) ||
            string.IsNullOrWhiteSpace(payload.TranscriptPath) ||
            payload.LastAssistantMessage is null ||
            payload.StopHookActive is null)
            throw new InvalidDataException("Stop-hook payload is missing one or more required fields.");
    }

    private static void ValidateRecord(ClaudeRunDirectory run, ClaudeCompletionRecord record)
    {
        if (record.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported completion schema version: {record.SchemaVersion}.");
        if (!string.Equals(record.RunId, run.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Completion record does not belong to the expected run.");
        if (string.IsNullOrWhiteSpace(record.ClaudeSessionId) || string.IsNullOrWhiteSpace(record.Cwd) ||
            string.IsNullOrWhiteSpace(record.TranscriptPath) || record.LastAssistantMessage is null)
            throw new InvalidDataException("Completion record is missing required data.");
    }
}

public static class ClaudeStopHookBridge
{
    public static async Task<int> RunAsync(
        string runDirectory,
        string runId,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ClaudeRunDirectory run;
        try
        {
            run = ClaudeRunDirectory.Open(runDirectory, runId);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            await stderr.WriteLineAsync(ex.Message);
            return (int)ClaudeStopHookExitCode.InvalidArguments;
        }

        ClaudeStopHookPayload? payload;
        try
        {
            var json = await stdin.ReadToEndAsync(cancellationToken);
            payload = JsonSerializer.Deserialize(json, ClaudeHookJsonContext.Default.ClaudeStopHookPayload);
            if (payload is null) throw new JsonException("Payload was null.");
            await new ClaudeCompletionStore().WriteAsync(run, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await stderr.WriteLineAsync(ex.Message);
            return (int)ClaudeStopHookExitCode.InvalidPayload;
        }
        catch (InvalidOperationException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return (int)ClaudeStopHookExitCode.ConflictingCompletion;
        }
        catch (IOException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return (int)ClaudeStopHookExitCode.IoFailure;
        }

        await stdout.WriteAsync("{}");
        return (int)ClaudeStopHookExitCode.Success;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ClaudeStopHookPayload))]
[JsonSerializable(typeof(ClaudeCompletionRecord))]
[JsonSerializable(typeof(ClaudeHookSettings))]
[JsonSerializable(typeof(Dictionary<string, ClaudeHookMatcher[]>))]
[JsonSerializable(typeof(ClaudeHookMatcher[]))]
[JsonSerializable(typeof(ClaudeHookDefinition[]))]
internal partial class ClaudeHookJsonContext : JsonSerializerContext;
