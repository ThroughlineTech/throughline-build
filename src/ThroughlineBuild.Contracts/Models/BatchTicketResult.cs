using System.Text.Json.Serialization;

namespace ThroughlineBuild.Contracts.Models;

public record BatchTicketResult(
    [property: JsonPropertyName("ticket_id")]
    string TicketId,
    [property: JsonPropertyName("commit_sha")]
    string CommitSha,
    [property: JsonPropertyName("stack_position")]
    int StackPosition,
    [property: JsonPropertyName("files_changed")]
    IReadOnlyList<string> FilesChanged,
    [property: JsonPropertyName("summary_ref")]
    string SummaryRef);
