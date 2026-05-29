using System.Net;
using System.Text.Json;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.ModelClient;
using Xunit;

namespace ThroughlineBuild.Anthropic.Tests;

public class AnthropicModelClientRetryTests
{
    private static ProviderConfig MakeConfig(string apiKey = "test-key") =>
        new ProviderConfig(
            BaseUrl: "https://api.anthropic.com",
            AuthScheme: "x-api-key",
            ExtraHeaders: new Dictionary<string, string>
            {
                ["x-api-key"] = apiKey,
                ["anthropic-version"] = "2023-06-01"
            },
            Vendor: "anthropic",
            DefaultTimeout: TimeSpan.FromSeconds(30)
        );

    [Fact]
    public async Task SendAsync_SuccessOnFirstAttempt_ReturnsResponse()
    {
        var wireResponse = new AnthropicModelClientResponse(
            Content: new List<AnthropicContentBlock> { new("text", "Success") },
            StopReason: "end_turn",
            Model: "claude-opus-4-5",
            Usage: new AnthropicUsage(10, 5, null, null)
        );
        var handler = new FakeRetryHandler(HttpStatusCode.OK, wireResponse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        var response = await client.SendAsync(request);

        Assert.Equal("Success", ((TextContent)response.Content[0]).Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_RetryOn429_SucceedsOnSecondAttempt()
    {
        var wireResponse = new AnthropicModelClientResponse(
            Content: new List<AnthropicContentBlock> { new("text", "Success after 429") },
            StopReason: "end_turn",
            Model: "claude-opus-4-5",
            Usage: new AnthropicUsage(10, 5, null, null)
        );
        var handler = new FakeRetryHandler(
            HttpStatusCode.OK,
            wireResponse,
            failureStatusCode: HttpStatusCode.TooManyRequests,
            failureCount: 1);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        var response = await client.SendAsync(request);

        Assert.Equal("Success after 429", ((TextContent)response.Content[0]).Text);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_RetryOn500_SucceedsOnSecondAttempt()
    {
        var wireResponse = new AnthropicModelClientResponse(
            Content: new List<AnthropicContentBlock> { new("text", "Success after 500") },
            StopReason: "end_turn",
            Model: "claude-opus-4-5",
            Usage: new AnthropicUsage(10, 5, null, null)
        );
        var handler = new FakeRetryHandler(
            HttpStatusCode.OK,
            wireResponse,
            failureStatusCode: HttpStatusCode.InternalServerError,
            failureCount: 1);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        var response = await client.SendAsync(request);

        Assert.Equal("Success after 500", ((TextContent)response.Content[0]).Text);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_ExhaustsRetriesOn429_ThrowsExceptionAfterThirdAttempt()
    {
        var handler = new FakeRetryHandler(
            HttpStatusCode.OK,
            null,
            failureStatusCode: HttpStatusCode.TooManyRequests,
            failureCount: 10);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        await Assert.ThrowsAsync<AnthropicApiException>(() => client.SendAsync(request));
        Assert.Equal(4, handler.CallCount); // 1 initial + 3 retries
    }

    [Fact]
    public async Task SendAsync_ExhaustsRetriesOn500_ThrowsExceptionAfterThirdAttempt()
    {
        var handler = new FakeRetryHandler(
            HttpStatusCode.OK,
            null,
            failureStatusCode: HttpStatusCode.InternalServerError,
            failureCount: 10);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        await Assert.ThrowsAsync<AnthropicApiException>(() => client.SendAsync(request));
        Assert.Equal(4, handler.CallCount); // 1 initial + 3 retries
    }

    [Fact]
    public async Task SendAsync_NoRetryOn400_FailsImmediately()
    {
        var handler = new FakeRetryHandler(
            HttpStatusCode.OK,
            null,
            failureStatusCode: HttpStatusCode.BadRequest,
            failureCount: 10);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 100
        );

        await Assert.ThrowsAsync<AnthropicApiException>(() => client.SendAsync(request));
        Assert.Equal(1, handler.CallCount); // No retry on 4xx
    }

    private class FakeRetryHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _successStatusCode;
        private readonly AnthropicModelClientResponse? _successResponse;
        private readonly HttpStatusCode? _failureStatusCode;
        private readonly int _failureCount;
        private int _callCount = 0;

        public int CallCount => _callCount;

        public FakeRetryHandler(
            HttpStatusCode successStatusCode,
            AnthropicModelClientResponse? successResponse,
            HttpStatusCode? failureStatusCode = null,
            int failureCount = 0)
        {
            _successStatusCode = successStatusCode;
            _successResponse = successResponse;
            _failureStatusCode = failureStatusCode;
            _failureCount = failureCount;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _callCount++;

            // Return failure for the specified number of attempts
            if (_callCount <= _failureCount && _failureStatusCode.HasValue)
            {
                return new HttpResponseMessage(_failureStatusCode.Value)
                {
                    Content = new StringContent("{\"error\":\"retryable error\"}")
                };
            }

            // Otherwise return success
            if (_successResponse != null)
            {
                var json = JsonSerializer.Serialize(
                    _successResponse,
                    AnthropicJsonContext.Default.AnthropicModelClientResponse);
                return new HttpResponseMessage(_successStatusCode)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(_successStatusCode)
            {
                Content = new StringContent("{\"error\":\"unexpected\"}")
            };
        }
    }
}
