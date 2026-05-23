using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.JudgmentSlots;

public sealed class ReasonTranslator
{
    private readonly ILlmClient _llm;

    public const string SystemPrompt =
        "Translate the following text to English if it is not already in English. " +
        "If it is already in English, return it unchanged. " +
        "Return only the translated text with no preamble or explanation.";

    public const string ModelId = "claude-haiku-4-5-20251001";

    public ReasonTranslator(ILlmClient llm) { _llm = llm; }

    public async Task<string> TranslateAsync(string reason, CancellationToken ct)
    {
        var messages = new[] { new LlmMessage("user", reason) };
        var options = new InvocationOptions(MaxTokens: 1024, Temperature: 0.0, System: SystemPrompt);
        var response = await _llm.InvokeAsync(ModelId, messages, options, ct);
        return response.Content.Trim();
    }
}
