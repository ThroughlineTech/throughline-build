using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.ClaudeCode;

/// <summary>
/// Per-call options for a Claude Code run.
/// </summary>
public sealed class ClaudeCodeRunOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public string? DebugCaptureDirectory { get; init; }
    public TextWriter? LiveStdoutSink { get; init; }
    public TextWriter? LiveStderrSink { get; init; }
    public TextWriter? ProgressDigestSink { get; init; }
    public WorkerSize Size { get; init; } = WorkerSize.Medium;
    public DebugTranscriptContext? DebugTranscript { get; init; }
    public bool LeanPlanning { get; init; }

    /// <summary>
    /// When true, <see cref="ClaudeCodeClient.RunAsync(string,string,ClaudeCodeRunOptions?,CancellationToken)"/>
    /// appends the worker-result contract if the instruction does not already mention
    /// WORKER_RESULT.
    /// </summary>
    public bool AppendWorkerResultContract { get; init; } = true;

    public string TicketId { get; init; } = "claude-code-run";
    public Phase Phase { get; init; } = Phase.Command;
    public IReadOnlyList<string> RelevantFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedWrites { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Context { get; init; } =
        new Dictionary<string, string>();

    internal WorkerOptions ToWorkerOptions() => new(
        Timeout,
        AllowedTools,
        EnvironmentVariables,
        DebugCaptureDirectory,
        LiveStdoutSink,
        LiveStderrSink,
        ProgressDigestSink,
        Size,
        DebugTranscript,
        LeanPlanning);
}
