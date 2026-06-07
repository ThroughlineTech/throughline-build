using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.JudgmentSlots;

public sealed class ReasonTranslator
{
    private readonly ILlmClient _llm;
    private readonly string _modelId;

    // Backed by an embedded template (translate-reason-prompt.md) rather than a compile-time
    // literal so the prompt text lives in exactly one place. `const` cannot be backed by a
    // runtime resource, so this is a cached static with the identical value and accessibility.
    public static string SystemPrompt { get; } = TranslateReasonPromptLoader.Load();

    public const string ModelId = "claude-haiku-4-5-20251001";

    public ReasonTranslator(ILlmClient llm) : this(llm, ModelId) { }

    public ReasonTranslator(ILlmClient llm, string modelId)
    {
        _llm = llm;
        _modelId = modelId;
    }

    public async Task<string> TranslateAsync(string reason, CancellationToken ct)
    {
        var messages = new[] { new LlmMessage("user", reason) };
        var options = new InvocationOptions(MaxTokens: 1024, Temperature: 0.0, System: SystemPrompt);
        var response = await _llm.InvokeAsync(_modelId, messages, options, ct);
        return response.Content.Trim();
    }
}
