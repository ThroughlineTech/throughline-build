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
