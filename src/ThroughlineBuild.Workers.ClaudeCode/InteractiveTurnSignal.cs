using System.Text;

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

    /// <summary>
    /// Best-effort, human-readable diagnosis of why no turn was detected, for the
    /// Phase-1 timeout artifact. Never throws.
    /// </summary>
    string DescribeCandidates(string workingDirectory, DateTimeOffset launchedAt);
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

    public async Task WaitForTurnAsync(string workingDirectory, DateTimeOffset launchedAt, CancellationToken cancellationToken)
    {
        var target = ClaudeTranscriptLocator.NormalizeDirectory(workingDirectory);
        var projects = Path.Combine(_configHome, "projects");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TurnEnded(projects, target, launchedAt)) return;
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public string DescribeCandidates(string workingDirectory, DateTimeOffset launchedAt)
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
                    report.Append(ClaudeTranscriptLocator.DescribeCandidates(dir, workingDirectory, launchedAt));
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

    private static bool TurnEnded(string projectsRoot, string targetDirectory, DateTimeOffset launchedAt)
    {
        // Tolerant by contract: any IO/parse failure means "not yet", never an
        // exception out of the poll loop.
        try
        {
            if (!Directory.Exists(projectsRoot)) return false;
            // No mtime gate on candidate selection: claude's first transcript write
            // can lag launch by ~60s while it connects MCP servers, so a strict
            // written >= launchedAt window dropped the real transcript on Linux. The
            // cwd + end_turn match is already run-specific; recency only orders the
            // scan so a fresh transcript is seen before stale ones.
            var candidates = Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => (Path: path, Written: SafeLastWrite(path)))
                .OrderByDescending(file => file.Written);

            foreach (var candidate in candidates)
            {
                if (ClaudeTranscriptLocator.TranscriptEndedTurn(candidate.Path, targetDirectory)) return true;
            }
            return false;
        }
        catch
        {
            return false;
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
