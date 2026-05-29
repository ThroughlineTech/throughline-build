using System.Text.Json.Serialization;

namespace ThroughlineBuild.ModelClient;

[JsonSerializable(typeof(ModelRequest))]
[JsonSerializable(typeof(ModelMessage))]
[JsonSerializable(typeof(ModelResponse))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(UsageDelta))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(List<ModelMessage>))]
[JsonSerializable(typeof(List<ContentBlock>))]
[JsonSerializable(typeof(List<ToolDefinition>))]
[JsonSerializable(typeof(List<string>))]
public partial class ModelClientJsonContext : JsonSerializerContext { }
