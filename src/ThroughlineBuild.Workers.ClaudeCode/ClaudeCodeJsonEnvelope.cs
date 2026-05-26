using System.Text.Json;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Wire-format DTO for the Claude Code CLI JSON envelope.
// Emitted by --output-format stream-json as the terminal NDJSON line
// (type=result); the legacy --output-format json single-blob output has
// bit-for-bit the same shape, so the same record handles both formats.
// Field names match the snake_case keys the CLI emits.
public record ClaudeCodeJsonEnvelope(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("is_error")] bool IsError,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("usage")] ClaudeCodeUsage? Usage
);

// Usage block nested inside the Claude Code JSON envelope.
// All token fields are nullable int to tolerate vendor drift across CLI versions.
public record ClaudeCodeUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens
);

// Diagnostic DTO for --debug capture: serializes the core WorkerResult fields
// excluding Metadata (IReadOnlyDictionary<string, object>) which is not AOT-serializable.
// Captured to worker-result.json under the debug capture directory.
public record WorkerResultDebugDto(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason);

// DTO for deserializing the WORKER_RESULT JSON block emitted by agent workers.
// File-scoped so it can be registered with ClaudeCodeJsonContext for source-gen.
// AOT trap: Dictionary<string, object> is not AOT-serializable; use
// Dictionary<string, JsonElement> instead. Callers that read metadata values
// should handle JsonElement (the phases already do via their TryGetString helpers).
internal sealed class WorkerResultDto
{
    // [JsonConverter] is required here: source-gen does not inherit the
    // CamelCase enum policy from JsonSerializerOptions; it must be wired
    // per-property. JsonStringEnumConverter<T> performs case-insensitive
    // matching on read, so PascalCase worker output ("Ok", "NeedsRework",
    // "Failed", "Escalate") and camelCase output are both accepted.
    // Status is nullable so the parser can detect a missing 'status' key
    // and fail loudly rather than silently defaulting to Ok (the enum zero).
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
    public Status? Status { get; set; }
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
    [JsonPropertyName("files_changed")]
    public List<string>? FilesChanged { get; set; }
    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; set; }
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

[JsonSerializable(typeof(ClaudeCodeJsonEnvelope))]
[JsonSerializable(typeof(ClaudeCodeUsage))]
[JsonSerializable(typeof(WorkerResultDto))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(ClaudeCodeSystemEvent))]
[JsonSerializable(typeof(ClaudeCodeAssistantEvent))]
[JsonSerializable(typeof(ClaudeCodeAssistantMessage))]
[JsonSerializable(typeof(ClaudeCodeContentBlock))]
[JsonSerializable(typeof(ClaudeCodeUserEvent))]
[JsonSerializable(typeof(List<ClaudeCodeContentBlock>))]
internal partial class ClaudeCodeJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(WorkerResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class DebugCaptureJsonContext : JsonSerializerContext { }
