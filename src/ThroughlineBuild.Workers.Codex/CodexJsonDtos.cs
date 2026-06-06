using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.Codex;

// Placeholder DTO for the Codex CLI result. The exact JSON shape will be
// confirmed and filled in by TLB-206 during CodexAgent.ExecuteAsync implementation.
public record CodexResultEnvelope(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("is_error")] bool IsError,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("usage")] CodexUsage? Usage,
    [property: JsonPropertyName("total_cost_usd")] decimal? TotalCostUsd
);

public record CodexUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens
);

public record CodexResultDebugDto(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason);

[JsonSerializable(typeof(CodexResultEnvelope))]
[JsonSerializable(typeof(CodexUsage))]
[JsonSerializable(typeof(CodexResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class CodexJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

[JsonSerializable(typeof(CodexResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class CodexDebugCaptureJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

// Deserialization DTOs for `codex debug models` output. The CLI emits a single
// JSON object whose "models" array lists every model the binary knows about;
// "visibility" == "list" marks operator-selectable models, other values
// (e.g. "hide") mark internal ones.
public record CodexDebugModelsEnvelope(
    [property: JsonPropertyName("models")] List<CodexDebugModelDto>? Models);

public record CodexDebugModelDto(
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("default_reasoning_level")] string? DefaultReasoningLevel,
    [property: JsonPropertyName("supported_reasoning_levels")] List<CodexReasoningLevelDto>? SupportedReasoningLevels,
    [property: JsonPropertyName("visibility")] string? Visibility);

public record CodexReasoningLevelDto(
    [property: JsonPropertyName("effort")] string? Effort);

[JsonSerializable(typeof(CodexDebugModelsEnvelope))]
[JsonSerializable(typeof(CodexDebugModelDto))]
[JsonSerializable(typeof(CodexReasoningLevelDto))]
[JsonSerializable(typeof(List<CodexDebugModelDto>))]
[JsonSerializable(typeof(List<CodexReasoningLevelDto>))]
internal partial class CodexProbeJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
