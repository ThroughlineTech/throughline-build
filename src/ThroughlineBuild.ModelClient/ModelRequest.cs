using System.Text.Json;

namespace ThroughlineBuild.ModelClient;

public record ModelRequest(
    string Model,
    IReadOnlyList<ModelMessage> Messages,
    int MaxTokens,
    string? SystemPrompt = null,
    double? Temperature = null,
    IReadOnlyList<string>? StopSequences = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    bool Stream = false
);

public record ModelMessage(
    string Role,
    IReadOnlyList<ContentBlock> Content
);

public abstract record ContentBlock(string Type);

public record TextContent(string Text) : ContentBlock("text");

public record ToolUseContent(string Id, string Name, JsonElement Input) : ContentBlock("tool_use");

public record ToolResultContent(string ToolUseId, IReadOnlyList<ContentBlock> Content, bool? IsError = null) : ContentBlock("tool_result");

public record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema
);
