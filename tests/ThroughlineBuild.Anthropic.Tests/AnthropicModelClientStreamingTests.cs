using System.Net;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.ModelClient;
using Xunit;

namespace ThroughlineBuild.Anthropic.Tests;

public class AnthropicModelClientStreamingTests
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

    private static readonly string CannedSse =
        "event: message_start\r\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":10}}}\r\n" +
        "\r\n" +
        "event: content_block_delta\r\n" +
        "data: {\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\r\n" +
        "\r\n" +
        "event: message_delta\r\n" +
        "data: {\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}\r\n" +
        "\r\n" +
        "event: message_stop\r\n" +
        "data: {\"type\":\"message_stop\"}\r\n" +
        "\r\n";

    private static ModelRequest MakeRequest() =>
        new ModelRequest(
            Model: "claude-sonnet-4-6",
            Messages: new[] { new ModelMessage("user", new[] { new TextContent("Hi") }) },
            MaxTokens: 100
        );

    [Fact]
    public async Task StreamAsync_CannedSse_YieldsCorrectEventSequence()
    {
        var handler = new SseHandler(HttpStatusCode.OK, CannedSse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var events = new List<ModelStreamEvent>();
        await foreach (var evt in client.StreamAsync(MakeRequest()))
            events.Add(evt);

        Assert.Equal(4, events.Count);

        var msgStart = Assert.IsType<MessageStartEvent>(events[0]);
        Assert.Equal("claude-sonnet-4-6", msgStart.Model);

        var delta = Assert.IsType<ContentDeltaEvent>(events[1]);
        Assert.Equal(0, delta.Index);
        var textContent = Assert.IsType<TextContent>(delta.Delta);
        Assert.Equal("Hello", textContent.Text);

        var msgDelta = Assert.IsType<MessageDeltaEvent>(events[2]);
        Assert.Equal("end_turn", msgDelta.StopReason);
        Assert.NotNull(msgDelta.Usage);
        Assert.Equal(5, msgDelta.Usage!.OutputTokens);

        Assert.IsType<MessageStopEvent>(events[3]);
    }

    [Fact]
    public async Task StreamAsync_NonSuccessResponse_ThrowsAnthropicApiException()
    {
        var handler = new SseHandler(HttpStatusCode.Unauthorized, string.Empty);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var ex = await Assert.ThrowsAsync<AnthropicApiException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(MakeRequest())) { }
        });

        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task StreamAsync_CancellationMidStream_StopsYielding()
    {
        var longSse =
            "event: message_start\r\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":10}}}\r\n" +
            "\r\n" +
            "event: content_block_delta\r\n" +
            "data: {\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\r\n" +
            "\r\n" +
            "event: content_block_delta\r\n" +
            "data: {\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" World\"}}\r\n" +
            "\r\n" +
            "event: message_stop\r\n" +
            "data: {\"type\":\"message_stop\"}\r\n" +
            "\r\n";

        var handler = new SseHandler(HttpStatusCode.OK, longSse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        using var cts = new CancellationTokenSource();
        var events = new List<ModelStreamEvent>();

        await foreach (var evt in client.StreamAsync(MakeRequest(), cts.Token))
        {
            events.Add(evt);
            if (evt is ContentDeltaEvent)
                cts.Cancel();
        }

        Assert.True(events.Count < 4, "Expected cancellation to stop stream before message_stop");
    }

    [Fact]
    public async Task StreamAsync_ErrorEvent_StopsAfterErrorEvent()
    {
        var errorSse =
            "event: message_start\r\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":10}}}\r\n" +
            "\r\n" +
            "event: error\r\n" +
            "data: {\"type\":\"error\",\"error\":{\"message\":\"overloaded\"}}\r\n" +
            "\r\n" +
            "event: message_stop\r\n" +
            "data: {\"type\":\"message_stop\"}\r\n" +
            "\r\n";

        var handler = new SseHandler(HttpStatusCode.OK, errorSse);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var events = new List<ModelStreamEvent>();
        await foreach (var evt in client.StreamAsync(MakeRequest()))
            events.Add(evt);

        Assert.Equal(2, events.Count);
        Assert.IsType<MessageStartEvent>(events[0]);
        Assert.IsType<ErrorEvent>(events[1]);
    }

    [Fact]
    public async Task StreamAsync_FixtureSse_YieldsCorrectEventSequence()
    {
        var fixtureContent = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "anthropic-sse-basic.txt"));
        var handler = new SseHandler(HttpStatusCode.OK, fixtureContent);
        var client = new AnthropicModelClient(new HttpClient(handler), MakeConfig());

        var events = new List<ModelStreamEvent>();
        await foreach (var evt in client.StreamAsync(MakeRequest()))
            events.Add(evt);

        var hasTextDelta = events.OfType<ContentDeltaEvent>().Any(e =>
        {
            var textContent = e.Delta as TextContent;
            return textContent?.Text == "Hello!";
        });
        Assert.True(hasTextDelta, "Expected at least one TextDelta event with text 'Hello!'");

        var hasMessageStop = events.OfType<MessageStopEvent>().Any();
        Assert.True(hasMessageStop, "Expected a MessageStop event at the end");
    }

    private class SseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public SseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(_body);
            var content = new System.Net.Http.ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }
}
