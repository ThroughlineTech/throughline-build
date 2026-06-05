using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Contracts.Models;

public record BatchWorkerResult(
    [property: JsonPropertyName("status")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    Status Status,
    [property: JsonPropertyName("summary")]
    string Summary,
    [property: JsonPropertyName("files_changed")]
    IReadOnlyList<string> FilesChanged,
    [property: JsonPropertyName("failure_reason")]
    string? FailureReason,
    [property: JsonPropertyName("metadata")]
    IReadOnlyDictionary<string, JsonElement> Metadata,
    [property: JsonPropertyName("tickets")]
    IReadOnlyList<BatchTicketResult> Tickets,
    [property: JsonPropertyName("blocks")]
    IReadOnlyDictionary<string, string>? Blocks = null);
