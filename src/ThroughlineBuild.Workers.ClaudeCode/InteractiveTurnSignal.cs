using System.Text;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Learns when Claude's interactive assistant turn has finished WITHOUT relying on
/// the per-turn Stop hook. claude 2.1.177 does not reliably fire the <c>--settings</c>
/// Stop hook in interactive mode (Linux's O_NOCTTY/sdk-cli launch never fires it, even
/// on exit), so the transport tails the persisted transcript for an <c>end_turn</c>
/// assistant message and SYNTHESIZES the completion from that located transcript - it
/// no longer waits for the Stop hook to write completion.json. See
/// docs/heartbeat/evidence/stage-06-process-hardening.md.
/// </summary>
internal interface IInteractiveTurnSignal
{
    /// <summary>
    /// Completes with the PATH of the run's persisted transcript once its assistant
    /// turn has ended (an assistant message with <c>stop_reason</c> == <c>end_turn</c>
    /// whose recorded <c>cwd</c> matches the run AND that carries
    /// <paramref name="runNonce"/>), or never (until cancelled). Returns null only on
    /// cancellation paths that do not throw. The transport reads the returned
    /// transcript to synthesize the completion, so the path is load-bearing.
    ///
    /// <paramref name="runNonce"/> is the per-run correlation token the transport
    /// embeds in the initial prompt; requiring it is what stops a stale prior-run
    /// transcript (same worktree, same cwd) or a competing session's transcript from
    /// completing THIS run. <paramref name="launchedAt"/> is retained for the timeout
    /// diagnosis only - it no longer gates matching (the nonce is the per-run
    /// correlator, and claude's first transcript write can lag launch by ~60s).
    /// Must be cancellation-aware - the transport bounds this wait with the same linked
    /// timeout/cancellation token it uses for completion.
    /// </summary>
    Task<string?> WaitForTurnAsync(string workingDirectory, string runNonce, DateTimeOffset launchedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort, human-readable diagnosis of why no turn was detected, for the
    /// Phase-1 timeout artifact. Never throws.
    /// </summary>
    string DescribeCandidates(string workingDirectory, string runNonce, DateTimeOffset launchedAt);
}

/// <summary>
/// Default <see cref="IInteractiveTurnSignal"/>: polls Claude's persisted transcript
/// tree under <c>&lt;configHome&gt;/projects/</c> for a transcript whose <c>cwd</c>
/// matches the run's working directory and that carries an assistant message at
/// <c>end_turn</c>. The per-file cwd/end_turn match is shared with the transport's
/// telemetry resolve via <see cref="ClaudeTranscriptLocator"/> so both agree on which
/// file is "this run's transcript".
/// Tolerant exactly like <see cref="ClaudePersistedTranscriptReader"/>: unparseable
/// lines and missing fields are skipped, and the poll loop never throws.
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

    public async Task<string?> WaitForTurnAsync(string workingDirectory, string runNonce, DateTimeOffset launchedAt, CancellationToken cancellationToken)
    {
        var target = ClaudeTranscriptLocator.NormalizeDirectory(workingDirectory);
        var projects = Path.Combine(_configHome, "projects");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TurnEnded(projects, target, runNonce) is string matchedPath) return matchedPath;
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public string DescribeCandidates(string workingDirectory, string runNonce, DateTimeOffset launchedAt)
    {
        try
        {
            var projects = Path.Combine(_configHome, "projects");
            var report = new StringBuilder();
            report.Append("config_home=").Append(_configHome).Append('\n');
            // claude shards transcripts into a per-project subdirectory under
            // projects/, and the encoded name is not recomputed here, so describe
            // every per-project directory plus any files directly under projects/.
            if (Directory.Exists(projects))
            {
                foreach (var dir in EnumerateCandidateDirectories(projects))
                    report.Append(ClaudeTranscriptLocator.DescribeCandidates(dir, workingDirectory, runNonce, launchedAt));
            }
            else
            {
                report.Append("projects_root=").Append(projects).Append('\n');
                report.Append("projects_root_exists=false\n");
            }
            return report.ToString();
        }
        catch (Exception ex)
        {
            return $"describe_candidates_error={ex.Message}\n";
        }
    }

    // Returns the path of the newest transcript that carries this run's nonce AND whose
    // cwd matches the run AND that carries an assistant end_turn, or null if none yet.
    // The matched path is what the transport reads to synthesize the completion.
    private static string? TurnEnded(string projectsRoot, string targetDirectory, string runNonce)
    {
        // Tolerant by contract: any IO/parse failure means "not yet", never an
        // exception out of the poll loop.
        try
        {
            if (!Directory.Exists(projectsRoot)) return null;
            // No mtime gate on candidate selection: claude's first transcript write
            // can lag launch by ~60s while it connects MCP servers, so a strict
            // written >= launchedAt window dropped the real transcript on Linux. The
            // nonce + cwd + end_turn match is per-run (the nonce uniquely identifies
            // THIS run's transcript); recency only orders the scan so a fresh
            // transcript is seen before stale ones.
            var candidates = Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => (Path: path, Written: SafeLastWrite(path)))
                .OrderByDescending(file => file.Written);

            foreach (var candidate in candidates)
            {
                if (ClaudeTranscriptLocator.TranscriptEndedTurn(candidate.Path, targetDirectory, runNonce)) return candidate.Path;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // The projects/ root itself plus each immediate per-project subdirectory, so the
    // diagnosis covers wherever claude actually wrote (it sometimes flattens, sometimes
    // shards by encoded cwd).
    private static IEnumerable<string> EnumerateCandidateDirectories(string projectsRoot)
    {
        yield return projectsRoot;
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(projectsRoot); }
        catch { yield break; }
        foreach (var dir in subdirs) yield return dir;
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
