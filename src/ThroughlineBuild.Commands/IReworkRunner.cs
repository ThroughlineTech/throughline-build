using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Abstraction over ReworkPhase so ReworkCommand is testable without a real phase.
/// </summary>
public interface IReworkRunner
{
    Task<ReworkResult> RunAsync(
        string ticketId,
        string workingDirectory,
        ReworkPhaseOptions options,
        CancellationToken ct);
}
