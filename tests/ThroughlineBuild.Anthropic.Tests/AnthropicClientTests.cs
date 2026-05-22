using System.Net;
using System.Text.Json;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Anthropic.Tests;

public class AnthropicClientTests
{
    private readonly AnthropicOptions _options = new()
    {
        ApiKey = "test-api-key",
        ApiVersion = "2023-06-01",
        BaseUrl = "https://api.anthropic.com"
    };

    [Fact]
    public async Task InvokeAsync_SuccessfulRequest_ReturnsLlmResponse()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Hello, world!")
            },
            Usage: new AnthropicUsage(
                InputTokens: 10,
                OutputTokens: 5,
                CacheReadInputTokens: 0,
                CacheCreationInputTokens: 0
            )
        ));

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[]
        {
            new LlmMessage("user", "Hello")
        };
        var options = new InvocationOptions(MaxTokens: 100, Temperature: 0.7);

        // Act
        var response = await client.InvokeAsync("anthropic:claude-opus", messages, options, CancellationToken.None);

        // Assert
        Assert.Equal("Hello, world!", response.Content);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(5, response.Usage.OutputTokens);
        Assert.Equal(0, response.Usage.CacheReadTokens);
        Assert.Equal(0, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task InvokeAsync_ModelIdWithAnthropicPrefix_StripsPrefixBeforeApiCall()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Response")
            },
            Usage: new AnthropicUsage(10, 5, 0, 0)
        ));

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Hi") };
        var options = new InvocationOptions(null, null);

        // Act
        await client.InvokeAsync("anthropic:claude-sonnet-4-6", messages, options, CancellationToken.None);

        // Assert
        var requestBody = handler.LastRequestBody;
        var request = JsonSerializer.Deserialize<AnthropicRequest>(requestBody, AnthropicJsonContext.Default.AnthropicRequest);
        Assert.NotNull(request);
        Assert.Equal("claude-sonnet-4-6", request.Model);
    }

    [Fact]
    public async Task InvokeAsync_CacheTokens_MappedCorrectly()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Response")
            },
            Usage: new AnthropicUsage(
                InputTokens: 100,
                OutputTokens: 50,
                CacheReadInputTokens: 25,
                CacheCreationInputTokens: 10
            )
        ));

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Test") };
        var options = new InvocationOptions(null, null);

        // Act
        var response = await client.InvokeAsync("claude-opus", messages, options, CancellationToken.None);

        // Assert
        Assert.Equal(25, response.Usage.CacheReadTokens);
        Assert.Equal(10, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task InvokeAsync_ErrorResponse_ThrowsAnthropicApiException()
    {
        // Arrange
        var errorBody = "{\"error\": {\"message\": \"Invalid request\"}}";
        var handler = new FakeMessageHandler(HttpStatusCode.BadRequest, null, errorBody);

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Hi") };
        var options = new InvocationOptions(null, null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<AnthropicApiException>(
            () => client.InvokeAsync("claude-opus", messages, options, CancellationToken.None));

        Assert.Equal(400, ex.Status);
        Assert.Equal(errorBody, ex.Body);
    }

    [Fact]
    public async Task InvokeAsync_RetryOn429_SucceedsOnSecondAttempt()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Success after retry")
            },
            Usage: new AnthropicUsage(10, 5, 0, 0)
        ), retryAfterCount: 1);

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Test") };
        var options = new InvocationOptions(null, null);

        // Act
        var response = await client.InvokeAsync("claude-opus", messages, options, CancellationToken.None);

        // Assert
        Assert.Equal("Success after retry", response.Content);
    }

    [Fact]
    public async Task InvokeAsync_RetryOn500_SucceedsOnSecondAttempt()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Success after server error")
            },
            Usage: new AnthropicUsage(10, 5, 0, 0)
        ), retryAfterCount: 1, retryOnServerError: true);

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Test") };
        var options = new InvocationOptions(null, null);

        // Act
        var response = await client.InvokeAsync("claude-opus", messages, options, CancellationToken.None);

        // Assert
        Assert.Equal("Success after server error", response.Content);
    }

    [Fact]
    public async Task InvokeAsync_EmptyApiKey_StillSetsHeader()
    {
        // Arrange
        var options = new AnthropicOptions { ApiKey = string.Empty };
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>
            {
                new("text", "Response")
            },
            Usage: new AnthropicUsage(10, 5, 0, 0)
        ));

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, options);

        var messages = new[] { new LlmMessage("user", "Hi") };
        var invocationOptions = new InvocationOptions(null, null);

        // Act
        await client.InvokeAsync("claude-opus", messages, invocationOptions, CancellationToken.None);

        // Assert
        Assert.True(handler.LastRequest!.Headers.Contains("x-api-key"));
    }

    [Fact]
    public void InvokeStreamAsync_ThrowsNotImplementedException()
    {
        // Arrange
        var handler = new FakeMessageHandler(HttpStatusCode.OK, new AnthropicResponse(
            Content: new List<AnthropicContentBlock>(),
            Usage: new AnthropicUsage(0, 0, 0, 0)
        ));

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(httpClient, _options);

        var messages = new[] { new LlmMessage("user", "Hi") };
        var options = new InvocationOptions(null, null);

        // Act & Assert
        Assert.Throws<NotImplementedException>(
            () => client.InvokeStreamAsync("claude-opus", messages, options, CancellationToken.None));
    }

    /// <summary>
    /// Fake HttpMessageHandler for testing without real HTTP calls.
    /// Supports retries by returning error status codes for initial attempts.
    /// </summary>
    private class FakeMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _successStatusCode;
        private readonly AnthropicResponse? _successResponse;
        private readonly string? _errorBody;
        private readonly int _retryAfterCount;
        private readonly bool _retryOnServerError;
        private int _callCount = 0;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        public FakeMessageHandler(
            HttpStatusCode successStatusCode,
            AnthropicResponse? successResponse,
            string? errorBody = null,
            int retryAfterCount = 0,
            bool retryOnServerError = false)
        {
            _successStatusCode = successStatusCode;
            _successResponse = successResponse;
            _errorBody = errorBody;
            _retryAfterCount = retryAfterCount;
            _retryOnServerError = retryOnServerError;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            _callCount++;

            // If we should retry on this attempt, return error first
            if (_callCount <= _retryAfterCount)
            {
                var statusCode = _retryOnServerError ? HttpStatusCode.InternalServerError : HttpStatusCode.TooManyRequests;
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("{\"error\": \"rate limited or server error\"}")
                };
            }

            // Otherwise return success
            if (_successResponse != null)
            {
                var json = JsonSerializer.Serialize(_successResponse, AnthropicJsonContext.Default.AnthropicResponse);
                return new HttpResponseMessage(_successStatusCode)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(_successStatusCode)
            {
                Content = new StringContent(_errorBody ?? string.Empty)
            };
        }
    }
}
