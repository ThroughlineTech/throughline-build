using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Wire-format DTO for the Claude Code CLI JSON envelope.
// Emitted when claude is invoked with --print --output-format json.
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

[JsonSerializable(typeof(ClaudeCodeJsonEnvelope))]
[JsonSerializable(typeof(ClaudeCodeUsage))]
internal partial class ClaudeCodeJsonContext : JsonSerializerContext { }
