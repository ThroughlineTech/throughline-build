using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.JudgmentSlots;

namespace ThroughlineBuild.JudgmentSlots.Tests;

public class ReasonTranslatorTests
{
    private class FakeLlmClient : ILlmClient
    {
        private readonly string _responseContent;
        private readonly Exception? _exceptionToThrow;

        public string? CapturedModelId { get; private set; }
        public IReadOnlyList<LlmMessage>? CapturedMessages { get; private set; }
        public InvocationOptions? CapturedOptions { get; private set; }

        public FakeLlmClient(string responseContent)
        {
            _responseContent = responseContent;
            _exceptionToThrow = null;
        }

        public FakeLlmClient(Exception exceptionToThrow)
        {
            _responseContent = "";
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<LlmResponse> InvokeAsync(
            string modelId,
            IReadOnlyList<LlmMessage> messages,
            InvocationOptions options,
            CancellationToken ct)
        {
            CapturedModelId = modelId;
            CapturedMessages = messages;
            CapturedOptions = options;

            if (_exceptionToThrow != null)
            {
                throw _exceptionToThrow;
            }

            var usage = new LlmUsage(InputTokens: 10, OutputTokens: 5, CacheReadTokens: null, CacheWriteTokens: null);
            var response = new LlmResponse(Content: _responseContent, Usage: usage);
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
            string modelId,
            IReadOnlyList<LlmMessage> messages,
            InvocationOptions options,
            CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public async Task TranslateAsync_AlreadyEnglish_ReturnsUnchanged()
    {
        var fake = new FakeLlmClient("hello");
        var translator = new ReasonTranslator(fake);

        var result = await translator.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("hello", result);
        Assert.Equal(ReasonTranslator.ModelId, fake.CapturedModelId);
        Assert.Equal(ReasonTranslator.SystemPrompt, fake.CapturedOptions?.System);
    }

    [Fact]
    public async Task TranslateAsync_NonEnglish_ReturnsTranslated()
    {
        var fake = new FakeLlmClient("hello");
        var translator = new ReasonTranslator(fake);

        var result = await translator.TranslateAsync("hola", CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task TranslateAsync_ApiFailure_Bubbles()
    {
        var exception = new InvalidOperationException("api failed");
        var fake = new FakeLlmClient(exception);
        var translator = new ReasonTranslator(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task TranslateAsync_TrimsWhitespace()
    {
        var fake = new FakeLlmClient("  hello  ");
        var translator = new ReasonTranslator(fake);

        var result = await translator.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task TwoArgConstructor_PassesSuppliedModelId()
    {
        var fake = new FakeLlmClient("hello");
        var translator = new ReasonTranslator(fake, "custom-model-id");

        await translator.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("custom-model-id", fake.CapturedModelId);
    }
}
