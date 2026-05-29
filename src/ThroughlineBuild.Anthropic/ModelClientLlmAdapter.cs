using ThroughlineBuild.Contracts;
using ThroughlineBuild.ModelClient;

namespace ThroughlineBuild.Anthropic;

/// <summary>
/// Adapter that wraps an IModelClient and implements ILlmClient.
/// Converts between the ILlmClient and IModelClient abstractions.
/// </summary>
public class ModelClientLlmAdapter : ILlmClient
{
    private readonly IModelClient _inner;

    public ModelClientLlmAdapter(IModelClient inner)
    {
        _inner = inner;
    }

    public async Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
    {
        // Parse model ID: strip "anthropic:" prefix if present
        var actualModel = modelId.StartsWith("anthropic:")
            ? modelId.Substring("anthropic:".Length)
            : modelId;

        // Convert LlmMessage to ModelMessage
        var modelMessages = messages
            .Select(m => new ModelMessage(
                m.Role,
                new[] { new TextContent(m.Content) } as IReadOnlyList<ContentBlock>))
            .ToList();

        var request = new ModelRequest(
            Model: actualModel,
            Messages: modelMessages,
            MaxTokens: options.MaxTokens ?? 1024,
            SystemPrompt: options.System,
            Temperature: options.Temperature
        );

        var response = await _inner.SendAsync(request, ct);

        // Extract text content from the first content block
        var textContent = response.Content
            .OfType<TextContent>()
            .FirstOrDefault()
            ?.Text
            ?? string.Empty;

        // Map usage
        var usage = new LlmUsage(
            InputTokens: response.Usage.InputTokens,
            OutputTokens: response.Usage.OutputTokens,
            CacheReadTokens: response.Usage.CacheReadTokens,
            CacheWriteTokens: response.Usage.CacheCreateTokens
        );

        return new LlmResponse(textContent, usage);
    }

    public IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
    {
        throw new NotImplementedException("Streaming not yet implemented for ModelClientLlmAdapter");
    }
}
