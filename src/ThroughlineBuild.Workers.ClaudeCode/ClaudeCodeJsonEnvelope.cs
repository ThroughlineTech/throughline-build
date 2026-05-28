using System.Text.Json;
using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("usage")] ClaudeCodeUsage? Usage,
    [property: JsonPropertyName("total_cost_usd")] decimal? TotalCostUsd
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

[JsonSerializable(typeof(ClaudeCodeJsonEnvelope))]
[JsonSerializable(typeof(ClaudeCodeUsage))]
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
