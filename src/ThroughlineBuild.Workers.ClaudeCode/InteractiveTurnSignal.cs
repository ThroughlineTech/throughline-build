using System.Text.Json;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Learns when Claude's interactive assistant turn has finished WITHOUT relying on
/// the per-turn Stop hook. claude 2.1.170 does not fire the <c>--settings</c> Stop
/// hook per turn in interactive mode (only on session exit), so the transport tails
/// the persisted transcript for an <c>end_turn</c> assistant message, then writes
/// <c>/exit</c> to the pty so the Stop hook fires on exit. See
/// docs/heartbeat/evidence/stage-06-process-hardening.md.
/// </summary>
internal interface IInteractiveTurnSignal
{
    /// <summary>
    /// Completes when the assistant turn for this run has ended (an assistant message
    /// with <c>stop_reason</c> == <c>end_turn</c> in the run's transcript), or never
    /// (until cancelled). Must be cancellation-aware - the transport bounds this wait
    /// with the same linked timeout/cancellation token it uses for completion.
    /// </summary>
    Task WaitForTurnAsync(string workingDirectory, DateTimeOffset launchedAt, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IInteractiveTurnSignal"/>: polls Claude's persisted transcript
/// tree under <c>&lt;configHome&gt;/projects/</c> for a transcript whose <c>cwd</c>
/// matches the run's working directory and that carries an assistant message at
/// <c>end_turn</c>. Tolerant exactly like <see cref="ClaudePersistedTranscriptReader"/>:
/// unparseable lines and missing fields are skipped, and the poll loop never throws.
/// AOT-safe: JsonDocument/JsonElement only, no reflection-based deserialization.
/// </summary>
internal sealed class TranscriptTurnSignal : IInteractiveTurnSignal
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _configHome;
    private readonly TimeSpan _pollInterval;

    public TranscriptTurnSignal()
        : this(ResolveConfigHome(), DefaultPollInterval) { }

    // Test seam: a hermetic config home and a fast poll keep the unit test off the
    // real ~/.claude tree and quick.
    internal TranscriptTurnSignal(string configHome, TimeSpan pollInterval)
    {
        _configHome = configHome;
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// CLAUDE_CONFIG_DIR if set, else <c>$HOME/.claude</c> (USERPROFILE on Windows).
    /// Transcripts live under <c>&lt;configHome&gt;/projects/</c>.
    /// </summary>
    internal static string ResolveConfigHome()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir)) return configDir;
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        // Fall back to the platform user-profile folder if the env var is unset.
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude");
    }

    public async Task WaitForTurnAsync(string workingDirectory, DateTimeOffset launchedAt, CancellationToken cancellationToken)
    {
        var target = NormalizeDirectory(workingDirectory);
        var projects = Path.Combine(_configHome, "projects");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TurnEnded(projects, target, launchedAt)) return;
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TurnEnded(string projectsRoot, string targetDirectory, DateTimeOffset launchedAt)
    {
        // Tolerant by contract: any IO/parse failure means "not yet", never an
        // exception out of the poll loop.
        try
        {
            if (!Directory.Exists(projectsRoot)) return false;
            // Newest first so a fresh transcript for this run is seen before older runs.
            var candidates = Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => (Path: path, Written: SafeLastWrite(path)))
                .Where(file => file.Written >= launchedAt)
                .OrderByDescending(file => file.Written);

            foreach (var candidate in candidates)
            {
                if (TranscriptForDirectoryEndedTurn(candidate.Path, targetDirectory)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TranscriptForDirectoryEndedTurn(string path, string targetDirectory)
    {
        var matchesDirectory = false;
        var sawEndTurn = false;
        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(rawLine); }
                catch (JsonException) { continue; }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    // Match the transcript to this run by its recorded cwd rather than
                    // trying to recompute claude's encoded project-dir name.
                    if (GetString(root, "cwd") is string cwd && DirectoriesMatch(cwd, targetDirectory))
                        matchesDirectory = true;

                    if (IsAssistantEndTurn(root)) sawEndTurn = true;
                }
            }
        }
        catch (IOException)
        {
            // Transcript mid-write (the file is being appended). Treat as not-yet.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return matchesDirectory && sawEndTurn;
    }

    private static bool IsAssistantEndTurn(JsonElement root)
    {
        if (GetString(root, "type") != "assistant") return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return false;
        return GetString(message, "stop_reason") == "end_turn";
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static bool DirectoriesMatch(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Canonicalize (resolving symlinks) so a /var worktree matches claude's
    // recorded /private/var cwd on macOS. ClaudeRealPath.Resolve falls back to
    // Path.GetFullPath when realpath fails, keeping this total. Both the passed
    // worktree and each transcript line's cwd flow through here, so the match is
    // symlink-proof regardless of which side carries the unresolved path.
    private static string NormalizeDirectory(string value)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(ClaudeRealPath.Resolve(value));
        }
        catch
        {
            return value;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
