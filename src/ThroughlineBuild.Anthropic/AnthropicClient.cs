using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Anthropic;

public class AnthropicClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly IAsyncPolicy<HttpResponseMessage> _resiliencePipeline;

    public AnthropicClient(HttpClient httpClient, AnthropicOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _resiliencePipeline = BuildResiliencePipeline();
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

        // Build the request payload
        var request = new AnthropicRequest(
            Model: actualModel,
            MaxTokens: options.MaxTokens ?? 1024,
            Messages: messages
                .Select(m => new AnthropicMessage(m.Role, m.Content))
                .ToList(),
            Temperature: options.Temperature,
            System: options.System
        );

        // Serialize using source generator
        var json = JsonSerializer.Serialize(request, AnthropicJsonContext.Default.AnthropicRequest);

        // Execute with resilience - create a fresh request for each attempt
        var response = await _resiliencePipeline.ExecuteAsync(
            async _ =>
            {
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/messages")
                {
                    Content = content
                };
                httpRequest.Headers.Add("x-api-key", _options.ApiKey);
                httpRequest.Headers.Add("anthropic-version", _options.ApiVersion);

                return await _httpClient.SendAsync(httpRequest, ct);
            },
            new Polly.Context());

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new AnthropicApiException((int)response.StatusCode, errorBody);
        }

        // Deserialize response
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var anthropicResponse = JsonSerializer.Deserialize(
            responseBody,
            AnthropicJsonContext.Default.AnthropicResponse)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic response");

        // Extract content from first text block
        var textContent = anthropicResponse.Content
            .FirstOrDefault(c => c.Type == "text")
            ?.Text
            ?? string.Empty;

        // Map usage
        var usage = new LlmUsage(
            InputTokens: anthropicResponse.Usage.InputTokens,
            OutputTokens: anthropicResponse.Usage.OutputTokens,
            CacheReadTokens: anthropicResponse.Usage.CacheReadInputTokens,
            CacheWriteTokens: anthropicResponse.Usage.CacheCreationInputTokens
        );

        return new LlmResponse(textContent, usage);
    }

    public IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
    {
        throw new NotImplementedException("Streaming not yet implemented for AnthropicClient");
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
