using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

// Serialization wrapper for WorkflowEvent lines in the JSONL event log.
// The 6 original fields preserve their existing PascalCase names for backward
// compatibility. The 4 new session-level fields use snake_case via
// [JsonPropertyName] per the TLB-147 schema spec. All new fields use
// [JsonIgnore(WhenWritingNull)] so sinks without a SessionContext emit output
// identical to the pre-TLB-147 format (forward-compatible schema extension).
internal sealed class EventLineDto
{
    public string? SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public EventKind Kind { get; init; }
    public string? TicketId { get; init; }
    public Phase Phase { get; init; }
    public IReadOnlyDictionary<string, object> Data { get; init; } = null!;

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; init; }

    [JsonPropertyName("project_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectName { get; init; }

    [JsonPropertyName("workspace_slug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceSlug { get; init; }

    [JsonPropertyName("build_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildVersion { get; init; }
}
