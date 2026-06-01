using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

/// <summary>
/// A null-object <see cref="ILlmClient"/> that returns the last user message
/// verbatim. Used as a graceful fallback for the close/defer/reopen verbs when
/// no real LLM client can be constructed (e.g. no Anthropic API key configured):
/// these verbs only use the LLM to translate the reason text to English via
/// <c>ReasonTranslator</c>, and that translation is non-essential. Echoing the
/// input is semantically identical to the translator's own "return it unchanged"
/// branch, so reasons that are already English (the common case) round-trip
/// untouched and the ticket transition still succeeds.
/// </summary>
public sealed class EchoLlmClient : ILlmClient
{
    public Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
    {
        var content = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        return Task.FromResult(new LlmResponse(content, new LlmUsage(0, 0, null, null)));
    }

    public IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct)
        => throw new NotSupportedException("EchoLlmClient does not support streaming.");
}
