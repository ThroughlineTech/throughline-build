namespace ThroughlineBuild.Contracts.Models;

public record WorkerResult(
    Status Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason,
    IReadOnlyDictionary<string, object> Metadata);

public enum Status { Ok, NeedsRework, Failed, Escalate }
