using System.Net;
using System.Text.Json;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.ModelClient;
using Xunit;

namespace ThroughlineBuild.Anthropic.Tests;

public class AnthropicModelClientTests
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
    public void AnthropicModelClient_Implements_IModelClient()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, null, "{\"error\":\"unused\"}");
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());
        Assert.IsAssignableFrom<IModelClient>(client);
    }

    [Fact]
    public async Task SendAsync_SuccessfulRequest_ReturnsMappedModelResponse()
    {
        var wireResponse = new AnthropicModelClientResponse(
            Content: new List<AnthropicContentBlock> { new("text", "Hello from Anthropic") },
            StopReason: "end_turn",
            Model: "claude-opus-4-5",
            Usage: new AnthropicUsage(20, 10, 5, 3)
        );
        var handler = new FakeHandler(HttpStatusCode.OK, wireResponse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hello") }) },
            MaxTokens: 256
        );

        var response = await client.SendAsync(request);

        Assert.Single(response.Content);
        Assert.IsType<TextContent>(response.Content[0]);
        Assert.Equal("Hello from Anthropic", ((TextContent)response.Content[0]).Text);
        Assert.Equal("end_turn", response.StopReason);
        Assert.Equal("claude-opus-4-5", response.Model);
        Assert.Equal(20, response.Usage.InputTokens);
        Assert.Equal(10, response.Usage.OutputTokens);
        Assert.Equal(5, response.Usage.CacheReadTokens);
        Assert.Equal(3, response.Usage.CacheCreateTokens);
        Assert.Equal("anthropic", response.Usage.Vendor);
    }

    [Fact]
    public async Task SendAsync_WithSystemPrompt_IncludesSystemInWireRequest()
    {
        var wireResponse = new AnthropicModelClientResponse(
            Content: new List<AnthropicContentBlock> { new("text", "ok") },
            StopReason: "end_turn",
            Model: "claude-sonnet-4-6",
            Usage: new AnthropicUsage(5, 2, null, null)
        );
        var handler = new FakeHandler(HttpStatusCode.OK, wireResponse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-sonnet-4-6",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("translate this") }) },
            MaxTokens: 100,
            SystemPrompt: "You are a translator."
        );

        await client.SendAsync(request);

        var body = handler.LastRequestBody;
        var sent = JsonSerializer.Deserialize<AnthropicModelClientRequest>(body, AnthropicJsonContext.Default.AnthropicModelClientRequest);
        Assert.NotNull(sent);
        Assert.Equal("You are a translator.", sent.System);
    }

    [Fact]
    public async Task SendAsync_NonSuccessResponse_ThrowsAnthropicApiException()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, null, "{\"error\":\"bad request\"}");
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var request = new ModelRequest(
            Model: "claude-opus-4-5",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("hi") }) },
            MaxTokens: 10
        );

        var ex = await Assert.ThrowsAsync<AnthropicApiException>(() => client.SendAsync(request));
        Assert.Equal(400, ex.Status);
    }

    private class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly AnthropicModelClientResponse? _successResponse;
        private readonly string? _errorBody;

        public string LastRequestBody { get; private set; } = string.Empty;

        public FakeHandler(HttpStatusCode status, AnthropicModelClientResponse? successResponse, string? errorBody = null)
        {
            _status = status;
            _successResponse = successResponse;
            _errorBody = errorBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_successResponse != null)
            {
                var json = JsonSerializer.Serialize(_successResponse, AnthropicJsonContext.Default.AnthropicModelClientResponse);
                return new HttpResponseMessage(_status) { Content = new StringContent(json) };
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_errorBody ?? string.Empty) };
        }
    }
}
