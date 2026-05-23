using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

public record PhaseResult(
    bool Success,
    string TicketId,
    Phase Phase,
    string? FailureReason,
    IReadOnlyDictionary<string, string> Outputs);

public interface IWorkflowPhase
{
    Phase Phase { get; }
    Task<PhaseResult> RunAsync(string ticketId, string workingDirectory, CancellationToken ct);
}
