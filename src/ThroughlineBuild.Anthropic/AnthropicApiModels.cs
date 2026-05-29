using System.Text.Json.Serialization;

namespace ThroughlineBuild.Anthropic;

public record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

public record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("system")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? System = null
);

public record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text
);

public record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens
);

public record AnthropicResponse(
    [property: JsonPropertyName("content")] List<AnthropicContentBlock> Content,
    [property: JsonPropertyName("usage")] AnthropicUsage Usage
);

public record AnthropicModelClientRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages,
    [property: JsonPropertyName("temperature")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Temperature,
    [property: JsonPropertyName("system")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? System = null
);

public record AnthropicModelClientResponse(
    [property: JsonPropertyName("content")] List<AnthropicContentBlock> Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] AnthropicUsage Usage
);

[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicResponse))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(AnthropicUsage))]
[JsonSerializable(typeof(List<AnthropicMessage>))]
[JsonSerializable(typeof(List<AnthropicContentBlock>))]
[JsonSerializable(typeof(AnthropicModelClientRequest))]
[JsonSerializable(typeof(AnthropicModelClientResponse))]
public partial class AnthropicJsonContext : JsonSerializerContext { }
