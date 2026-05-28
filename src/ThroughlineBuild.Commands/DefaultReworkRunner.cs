using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Production implementation of IReworkRunner. Delegates to a pre-constructed ReworkPhase.
/// </summary>
public sealed class DefaultReworkRunner : IReworkRunner
{
    private readonly ReworkPhase _phase;
    private readonly string _workingDirectory;

    public DefaultReworkRunner(ReworkPhase phase, string workingDirectory)
    {
        _phase = phase;
        _workingDirectory = workingDirectory;
    }

    public Task<ReworkResult> RunAsync(
        string ticketId,
        string workingDirectory,
        ReworkPhaseOptions options,
        CancellationToken ct)
    {
        // Use the caller-provided workingDirectory if supplied; fall back to constructor value.
        var wd = !string.IsNullOrEmpty(workingDirectory) ? workingDirectory : _workingDirectory;
        return _phase.RunAsync(ticketId, wd, ct);
    }
}
