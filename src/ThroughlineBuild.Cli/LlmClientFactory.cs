using ThroughlineBuild.Anthropic;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config, BuildSecrets secrets, HttpClient http)
    {
        if (string.IsNullOrEmpty(config.DefaultModel))
            throw new ConfigException(
                "LLM client required but [llm] default_model is not set in config.toml");

        if (config.DefaultModel.StartsWith("anthropic:", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(secrets.AnthropicApiKey))
                throw new ConfigException(
                    $"anthropic_api_key not set and env var '{config.AnthropicApiKeyEnv}' is not set; " +
                    "configure [llm] anthropic_api_key or set the env var");
            return new AnthropicClient(http, new AnthropicOptions { ApiKey = secrets.AnthropicApiKey });
        }

        var colonIdx = config.DefaultModel.IndexOf(':');
        var prefix = colonIdx >= 0
            ? config.DefaultModel[..colonIdx]
            : config.DefaultModel;
        throw new ConfigException(
            $"unsupported LLM vendor prefix '{prefix}' in [llm] default_model; only 'anthropic:' is supported");
    }
}
