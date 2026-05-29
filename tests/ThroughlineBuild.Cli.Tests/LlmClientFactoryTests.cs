using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class LlmClientFactoryTests
{
    [Fact]
    public void Create_AnthropicModelWithKey_ReturnsClient()
    {
        var config = new LlmConfig("anthropic:claude-haiku-4-5-20251001", "ANTHROPIC_API_KEY");
        var secrets = new BuildSecrets("plane-token", "sk-ant-test-key");

        var result = LlmClientFactory.Create(config, secrets, new HttpClient());

        Assert.NotNull(result);
        Assert.IsAssignableFrom<ILlmClient>(result);
    }

    [Fact]
    public void Create_AnthropicModelWithNullKey_ThrowsConfigExceptionWithEnvVarName()
    {
        var config = new LlmConfig("anthropic:claude-haiku-4-5-20251001", "ANTHROPIC_API_KEY");
        var secrets = new BuildSecrets("plane-token", null);

        var ex = Assert.Throws<ConfigException>(() => LlmClientFactory.Create(config, secrets, new HttpClient()));

        Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
    }

    [Fact]
    public void Create_UnsupportedVendorPrefix_ThrowsConfigExceptionWithUnsupported()
    {
        var config = new LlmConfig("openai:gpt-4", "ANTHROPIC_API_KEY");
        var secrets = new BuildSecrets("plane-token", null);

        var ex = Assert.Throws<ConfigException>(() => LlmClientFactory.Create(config, secrets, new HttpClient()));

        Assert.Contains("unsupported", ex.Message);
        Assert.Contains("openai", ex.Message);
    }

    [Fact]
    public void Create_EmptyDefaultModel_ThrowsConfigExceptionWithDefaultModel()
    {
        var config = new LlmConfig(string.Empty, "ANTHROPIC_API_KEY");
        var secrets = new BuildSecrets("plane-token", null);

        var ex = Assert.Throws<ConfigException>(() => LlmClientFactory.Create(config, secrets, new HttpClient()));

        Assert.Contains("default_model", ex.Message);
    }
}
