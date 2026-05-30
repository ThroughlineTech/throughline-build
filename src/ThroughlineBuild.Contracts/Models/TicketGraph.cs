namespace ThroughlineBuild.Contracts.Models;

public record TicketGraph(
    IReadOnlyList<IReadOnlyList<string>> Levels,
    bool CycleDetected,
    IReadOnlyList<string> CycleMembers);
