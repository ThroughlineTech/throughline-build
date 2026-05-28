using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.Gemini;

// Placeholder DTOs for the Gemini CLI result. The exact JSON shape will be
// confirmed and filled in by B02 during GeminiAgent.ExecuteAsync implementation.
public record GeminiResultEnvelope(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("stats")] GeminiUsage? Stats
);

public record GeminiUsage(
    [property: JsonPropertyName("tokens")] GeminiTokenStats? Tokens,
    [property: JsonPropertyName("tools")] JsonElement? Tools
);

public record GeminiTokenStats(
    [property: JsonPropertyName("total")] int? Total
);

public record GeminiResultDebugDto(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason);

[JsonSerializable(typeof(GeminiResultEnvelope))]
[JsonSerializable(typeof(GeminiUsage))]
[JsonSerializable(typeof(GeminiTokenStats))]
[JsonSerializable(typeof(GeminiResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class GeminiJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

[JsonSerializable(typeof(GeminiResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class GeminiDebugCaptureJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
