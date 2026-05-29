using System.Text.Json;
using ThroughlineBuild.ModelClient;

namespace ThroughlineBuild.Anthropic;

public class AnthropicModelClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ProviderConfig _config;

    public AnthropicModelClient(HttpClient httpClient, ProviderConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
    {
        var messages = request.Messages
            .Select(m => new AnthropicMessage(
                m.Role,
                m.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty))
            .ToList();

        var wireRequest = new AnthropicModelClientRequest(
            Model: request.Model,
            MaxTokens: request.MaxTokens,
            Messages: messages,
            Temperature: request.Temperature,
            System: request.SystemPrompt
        );

        var json = JsonSerializer.Serialize(wireRequest, AnthropicJsonContext.Default.AnthropicModelClientRequest);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/v1/messages")
        {
            Content = content
        };

        httpRequest.Headers.Add(_config.AuthScheme, _config.ExtraHeaders.TryGetValue(_config.AuthScheme, out var apiKey) ? apiKey : string.Empty);
        foreach (var header in _config.ExtraHeaders)
        {
            if (!httpRequest.Headers.Contains(header.Key))
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var response = await _httpClient.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new AnthropicApiException((int)response.StatusCode, errorBody);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var wireResponse = JsonSerializer.Deserialize(
            responseBody,
            AnthropicJsonContext.Default.AnthropicModelClientResponse)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic response");

        var contentBlocks = wireResponse.Content
            .Select<AnthropicContentBlock, ContentBlock>(b => new TextContent(b.Text ?? string.Empty))
            .ToList();

        var usage = new Usage(
            InputTokens: wireResponse.Usage.InputTokens,
            OutputTokens: wireResponse.Usage.OutputTokens,
            CacheReadTokens: wireResponse.Usage.CacheReadInputTokens,
            CacheCreateTokens: wireResponse.Usage.CacheCreationInputTokens,
            Model: wireResponse.Model,
            Vendor: _config.Vendor,
            Cost: null
        );

        return new ModelResponse(
            Content: contentBlocks,
            StopReason: wireResponse.StopReason ?? string.Empty,
            Model: wireResponse.Model,
            Usage: usage
        );
    }

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default)
    {
        throw new NotImplementedException("Streaming is implemented in AnthropicStreamingModelClient (TLB-245)");
    }
}
