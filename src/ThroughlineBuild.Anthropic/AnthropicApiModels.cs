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

public record AnthropicStreamRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("temperature")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Temperature,
    [property: JsonPropertyName("system")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? System = null
);

public record AnthropicSseMessageStart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] AnthropicSseMessage? Message
);

public record AnthropicSseMessage(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("usage")] AnthropicSseStartUsage? Usage
);

public record AnthropicSseStartUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens
);

public record AnthropicSseContentBlockDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] AnthropicSseDelta? Delta
);

public record AnthropicSseDelta(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text
);

public record AnthropicSseMessageDelta(
    [property: JsonPropertyName("delta")] AnthropicSseDeltaInfo? Delta,
    [property: JsonPropertyName("usage")] AnthropicSseDeltaUsage? Usage
);

public record AnthropicSseDeltaInfo(
    [property: JsonPropertyName("stop_reason")] string? StopReason
);

public record AnthropicSseDeltaUsage(
    [property: JsonPropertyName("output_tokens")] int OutputTokens
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
[JsonSerializable(typeof(AnthropicStreamRequest))]
[JsonSerializable(typeof(AnthropicSseMessageStart))]
[JsonSerializable(typeof(AnthropicSseMessage))]
[JsonSerializable(typeof(AnthropicSseStartUsage))]
[JsonSerializable(typeof(AnthropicSseContentBlockDelta))]
[JsonSerializable(typeof(AnthropicSseDelta))]
[JsonSerializable(typeof(AnthropicSseMessageDelta))]
[JsonSerializable(typeof(AnthropicSseDeltaInfo))]
[JsonSerializable(typeof(AnthropicSseDeltaUsage))]
public partial class AnthropicJsonContext : JsonSerializerContext { }
