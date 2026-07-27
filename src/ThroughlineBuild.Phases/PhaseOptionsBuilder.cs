using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Builds phase-scoped options and owns the debug session index.
/// </summary>
public sealed class PhaseOptionsBuilder
{
    private static readonly object DebugIndexLock = new();
    private readonly BuildOptions _baseOptions;

    public PhaseOptionsBuilder(BuildOptions baseOptions)
    {
        _baseOptions = baseOptions;
    }

    public BuildOptions BuildPhaseOptions(
        string sessionId,
        string ticketId,
        string phaseName,
        int? round = null,
        string? targetBranch = null)
    {
        var debugCaptureDirectory = ScopeDebugCaptureDirectory(
            _baseOptions.DebugCaptureDirectory,
            ticketId,
            phaseName,
            round,
            sessionId);

        if (_baseOptions.ProgressDigestSink is null)
        {
            return _baseOptions with
            {
                SessionId = sessionId,
                DebugCaptureDirectory = debugCaptureDirectory,
                TargetBranch = targetBranch ?? _baseOptions.TargetBranch
            };
        }

        return _baseOptions with
        {
            SessionId = sessionId,
            DebugCaptureDirectory = debugCaptureDirectory,
            ProgressDigestSink = new PrefixedTextWriter(
                $"[{ticketId}] ",
                _baseOptions.ProgressDigestSink),
            TargetBranch = targetBranch ?? _baseOptions.TargetBranch
        };
    }

    internal static string? ScopeDebugCaptureDirectory(
        string? parentDirectory,
        string ticketId,
        string phaseName,
        int? round,
        string sessionId)
    {
        if (parentDirectory is null)
            return null;

        var attemptSegment =
            round is null ? SafePathSegment(sessionId) : $"round-{round.Value}";
        var scopedDirectory = Path.Combine(
            parentDirectory,
            SafePathSegment(ticketId),
            SafePathSegment(phaseName),
            attemptSegment);

        WriteDebugSessionIndex(
            parentDirectory,
            ticketId,
            phaseName,
            round,
            sessionId,
            scopedDirectory);
        return scopedDirectory;
    }

    internal static void WriteDebugSessionIndex(
        string parentDirectory,
        string ticketId,
        string phaseName,
        int? round,
        string sessionId,
        string scopedDirectory)
    {
        try
        {
            Directory.CreateDirectory(parentDirectory);
            var relativePath = Path.GetRelativePath(
                parentDirectory,
                scopedDirectory);
            var roundLabel = round is null ? "-" : round.Value.ToString();
            var line =
                $"{DateTimeOffset.UtcNow:O}\t{ticketId}\t{phaseName}\t" +
                $"{roundLabel}\t{sessionId}\t{relativePath}" +
                Environment.NewLine;
            lock (DebugIndexLock)
            {
                File.AppendAllText(
                    Path.Combine(parentDirectory, "session-index.txt"),
                    line);
            }
        }
        catch
        {
            // Debug capture must never change phase behavior.
        }
    }

    internal static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "_" : sanitized;
    }
}
