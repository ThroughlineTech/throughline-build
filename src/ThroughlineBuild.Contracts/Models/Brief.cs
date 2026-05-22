namespace ThroughlineBuild.Contracts.Models;

public record Brief(
    string TicketId,
    Phase Phase,
    string Instruction,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> AllowedWrites,
    IReadOnlyDictionary<string, string> Context);
