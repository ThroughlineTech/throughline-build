using System.Text.Json;
using Polly;
using Polly.Retry;
using ThroughlineBuild.ModelClient;

namespace ThroughlineBuild.Anthropic;

public class AnthropicModelClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ProviderConfig _config;
    private readonly IAsyncPolicy<HttpResponseMessage> _resiliencePipeline;

    public AnthropicModelClient(HttpClient httpClient, ProviderConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _resiliencePipeline = BuildResiliencePipeline();
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

        var response = await _resiliencePipeline.ExecuteAsync(
            async _ =>
            {
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var httpRequest = BuildHttpRequest(content);
                return await _httpClient.SendAsync(httpRequest, ct);
            },
            new Polly.Context());

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

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = request.Messages
            .Select(m => new AnthropicMessage(
                m.Role,
                m.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty))
            .ToList();

        var wireRequest = new AnthropicStreamRequest(
            Model: request.Model,
            MaxTokens: request.MaxTokens,
            Messages: messages,
            Stream: true,
            Temperature: request.Temperature,
            System: request.SystemPrompt
        );

        var json = JsonSerializer.Serialize(wireRequest, AnthropicJsonContext.Default.AnthropicStreamRequest);

        var response = await _resiliencePipeline.ExecuteAsync(
            async _ =>
            {
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var httpRequest = BuildHttpRequest(content);
                return await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            },
            new Polly.Context());

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new AnthropicApiException((int)response.StatusCode, errorBody);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? currentEventType = null;
        string? currentData = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEventType = line.Substring(7);
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentData = line.Substring(6);
            }
            else if (line.Length == 0 && currentEventType != null && currentData != null)
            {
                var evt = MapSseEvent(currentEventType, currentData);
                if (evt != null)
                {
                    yield return evt;
                    if (evt is MessageStopEvent || evt is ErrorEvent)
                        yield break;
                }
                currentEventType = null;
                currentData = null;
            }
        }
    }

    private ModelStreamEvent? MapSseEvent(string eventType, string data)
    {
        switch (eventType)
        {
            case "content_block_delta":
                var blockDelta = JsonSerializer.Deserialize(data, AnthropicJsonContext.Default.AnthropicSseContentBlockDelta);
                if (blockDelta?.Delta?.Type == "text_delta" && blockDelta.Delta.Text != null)
                    return new ContentDeltaEvent(blockDelta.Index, new TextContent(blockDelta.Delta.Text));
                return null;

            case "message_delta":
                var msgDelta = JsonSerializer.Deserialize(data, AnthropicJsonContext.Default.AnthropicSseMessageDelta);
                var usageDelta = msgDelta?.Usage != null ? new UsageDelta(msgDelta.Usage.OutputTokens) : null;
                return new MessageDeltaEvent(msgDelta?.Delta?.StopReason, usageDelta);

            case "message_start":
                var msgStart = JsonSerializer.Deserialize(data, AnthropicJsonContext.Default.AnthropicSseMessageStart);
                return new MessageStartEvent(msgStart?.Message?.Model ?? string.Empty);

            case "message_stop":
                return new MessageStopEvent();

            case "error":
                return new ErrorEvent(data);

            default:
                return null;
        }
    }

    private HttpRequestMessage BuildHttpRequest(StringContent content)
    {
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

        return httpRequest;
    }

    private IAsyncPolicy<HttpResponseMessage> BuildResiliencePipeline()
    {
        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r =>
                r.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                (int)r.StatusCode >= 500)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        return retryPolicy;
    }
}
