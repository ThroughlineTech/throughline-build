namespace ThroughlineBuild.Contracts.Models;

public record BatchWorkerResult(
    Status Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason,
    IReadOnlyDictionary<string, object> Metadata,
    IReadOnlyList<BatchTicketResult> Tickets,
    IReadOnlyDictionary<string, string>? Blocks = null);

