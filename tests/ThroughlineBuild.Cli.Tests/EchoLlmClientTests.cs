using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.JudgmentSlots;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class EchoLlmClientTests
{
    private static readonly InvocationOptions Options = new(MaxTokens: 1024, Temperature: 0.0);

    [Fact]
    public async Task InvokeAsync_ReturnsLastUserMessageVerbatim()
    {
        var client = new EchoLlmClient();
        var messages = new[]
        {
            new LlmMessage("user", "first"),
            new LlmMessage("assistant", "ignored"),
            new LlmMessage("user", "subsumed by TLB-322"),
        };

        var response = await client.InvokeAsync("any-model", messages, Options, CancellationToken.None);

        Assert.Equal("subsumed by TLB-322", response.Content);
        Assert.Equal(0, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task InvokeAsync_NoUserMessage_ReturnsEmpty()
    {
        var client = new EchoLlmClient();
        var messages = new[] { new LlmMessage("assistant", "hi") };

        var response = await client.InvokeAsync("any-model", messages, Options, CancellationToken.None);

        Assert.Equal(string.Empty, response.Content);
    }

    [Fact]
    public void InvokeStreamAsync_Throws()
    {
        var client = new EchoLlmClient();

        Assert.Throws<NotSupportedException>(() =>
            client.InvokeStreamAsync("any-model", Array.Empty<LlmMessage>(), Options, CancellationToken.None));
    }

    // The bug this guards: with no API key, close/defer/reopen fall back to
    // EchoLlmClient. The reason must round-trip unchanged so the ticket still closes.
    [Fact]
    public async Task ReasonTranslator_OverEcho_ReturnsReasonUnchanged()
    {
        var translator = new ReasonTranslator(new EchoLlmClient());
        const string reason = "subsumed by TLB-322, which includes UserGuideLoader plus the verb";

        var result = await translator.TranslateAsync(reason, CancellationToken.None);

        Assert.Equal(reason, result);
    }
}
