using System.Text;
using System.Text.Json;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Resolves Claude's REAL on-disk transcript for a run by scanning the project
/// directory for the newest <c>*.jsonl</c> whose recorded <c>cwd</c> matches the
/// run's working directory. claude 2.1.177's Stop-hook payload can report a
/// <c>session_id</c>/<c>transcript_path</c> whose FILENAME does not match the actual
/// on-disk transcript for the same run, so neither the turn-detector nor the
/// telemetry reader can trust the payload filename: both resolve the file by
/// content cwd instead. Shared by <see cref="TranscriptTurnSignal"/> (turn match)
/// and the transport's completion parse (telemetry) so they always agree on which
/// file is "this run's transcript".
///
/// Tolerant by contract: any IO/parse failure on a single file or line is skipped,
/// never thrown out of the locator.
/// AOT-safe: JsonDocument/JsonElement only, no reflection-based deserialization.
/// </summary>
internal static class ClaudeTranscriptLocator
{
    /// <summary>
    /// Returns the newest <c>*.jsonl</c> under <paramref name="projectDir"/> whose
    /// content <c>cwd</c> (on ANY line) matches <paramref name="cwd"/>, or null if
    /// none. <paramref name="notBefore"/> filters out clearly-older transcripts;
    /// pass null to consider every file regardless of mtime (used by the telemetry
    /// resolve, where the file is known to exist and recency is not load-bearing).
    /// When supplied it is applied with a small tolerance so a file whose first
    /// write lagged the launch (claude connecting MCP servers) is not excluded.
    /// </summary>
    public static string? FindNewestMatching(string projectDir, string cwd, DateTimeOffset? notBefore)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                return null;
            var target = NormalizeDirectory(cwd);
            var floor = notBefore is DateTimeOffset bound ? bound - MtimeTolerance : (DateTimeOffset?)null;

            // Newest first so the freshest transcript for this run wins.
            var ordered = Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Written: SafeLastWrite(path)))
                .Where(file => floor is null || file.Written >= floor.Value)
                .OrderByDescending(file => file.Written);

            foreach (var candidate in ordered)
            {
                if (TranscriptMatchesDirectory(candidate.Path, target))
                    return candidate.Path;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if any line in the transcript records a <c>cwd</c> matching
    /// <paramref name="targetDirectory"/> (already normalized) AND an assistant
    /// <c>end_turn</c> is present. Used by the turn-detector.
    /// </summary>
    public static bool TranscriptEndedTurn(string path, string targetDirectory)
    {
        var matchesDirectory = false;
        var sawEndTurn = false;
        if (!ForEachLine(path, root =>
        {
            if (GetString(root, "cwd") is string cwd && DirectoriesMatch(cwd, targetDirectory, alreadyNormalizedRight: true))
                matchesDirectory = true;
            if (IsAssistantEndTurn(root)) sawEndTurn = true;
        }))
        {
            return false;
        }
        return matchesDirectory && sawEndTurn;
    }

    private static bool TranscriptMatchesDirectory(string path, string targetDirectory)
    {
        var matchesDirectory = false;
        if (!ForEachLine(path, root =>
        {
            if (GetString(root, "cwd") is string cwd && DirectoriesMatch(cwd, targetDirectory, alreadyNormalizedRight: true))
                matchesDirectory = true;
        }))
        {
            return false;
        }
        return matchesDirectory;
    }

    /// <summary>
    /// Human-readable, multi-line diagnosis of every candidate transcript under
    /// <paramref name="projectDir"/> for a run that launched at
    /// <paramref name="launchedAt"/> in <paramref name="cwd"/>. Used only on the
    /// Phase-1 timeout path so the next live run pins the exact match failure from
    /// artifacts. Best-effort: never throws.
    /// </summary>
    public static string DescribeCandidates(string projectDir, string cwd, DateTimeOffset launchedAt)
    {
        var report = new StringBuilder();
        try
        {
            var resolvedTarget = NormalizeDirectory(cwd);
            report.Append("projects_root=").Append(projectDir).Append('\n');
            report.Append("worktree_raw=").Append(cwd).Append('\n');
            report.Append("worktree_resolved=").Append(resolvedTarget).Append('\n');
            report.Append("launched_at=").Append(launchedAt.ToString("O")).Append('\n');

            if (!Directory.Exists(projectDir))
            {
                report.Append("projects_root_exists=false\n");
                return report.ToString();
            }

            var files = Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(SafeLastWrite)
                .ToList();
            report.Append("candidate_count=").Append(files.Count).Append('\n');

            foreach (var file in files)
            {
                var written = SafeLastWrite(file);
                var cwds = new List<string>();
                var matched = false;
                var sawEndTurn = false;
                ForEachLine(file, root =>
                {
                    if (GetString(root, "cwd") is string raw)
                    {
                        var resolved = NormalizeDirectory(raw);
                        var entry = raw == resolved ? raw : $"{raw} -> {resolved}";
                        if (!cwds.Contains(entry)) cwds.Add(entry);
                        if (DirectoriesMatch(raw, resolvedTarget, alreadyNormalizedRight: true)) matched = true;
                    }
                    if (IsAssistantEndTurn(root)) sawEndTurn = true;
                });

                report.Append("candidate=").Append(file).Append('\n');
                report.Append("  last_write=").Append(written.ToString("O"))
                    .Append(written >= launchedAt ? " (>=launch, included)" : " (<launch, excluded by mtime gate)").Append('\n');
                report.Append("  cwd_values=").Append(cwds.Count == 0 ? "(none)" : string.Join(" | ", cwds)).Append('\n');
                report.Append("  cwd_matched=").Append(matched).Append('\n');
                report.Append("  assistant_end_turn=").Append(sawEndTurn).Append('\n');
            }
        }
        catch (Exception ex)
        {
            report.Append("describe_error=").Append(ex.Message).Append('\n');
        }
        return report.ToString();
    }

    // Reads each non-empty line as a JSON object and invokes the visitor. Returns
    // false on an IO/access failure (file mid-write or unreadable) so callers treat
    // a partially-readable transcript as "not yet"; parse failures on a single line
    // are skipped without aborting.
    private static bool ForEachLine(string path, Action<JsonElement> visit)
    {
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
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                        visit(document.RootElement);
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAssistantEndTurn(JsonElement root)
    {
        if (GetString(root, "type") != "assistant") return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return false;
        return GetString(message, "stop_reason") == "end_turn";
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    // Clock-granularity / first-write-lag tolerance: claude's first transcript write
    // can lag launch by ~60s while it connects MCP servers, and mtime can read a hair
    // before launchedAt across the host clock. A generous floor never excludes a real
    // match yet still drops stale prior-run transcripts.
    private static readonly TimeSpan MtimeTolerance = TimeSpan.FromMinutes(5);

    private static bool DirectoriesMatch(string left, string right, bool alreadyNormalizedRight)
    {
        var normalizedRight = alreadyNormalizedRight ? right : NormalizeDirectory(right);
        return string.Equals(NormalizeDirectory(left), normalizedRight,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Canonicalize (resolving symlinks) so a /var worktree matches claude's recorded
    // /private/var cwd on macOS. ClaudeRealPath.Resolve falls back to Path.GetFullPath
    // when realpath fails, keeping this total.
    internal static string NormalizeDirectory(string value)
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
