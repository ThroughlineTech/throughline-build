using ThroughlineBuild.ModelClient;
using Xunit;

namespace ThroughlineBuild.ModelClient.Tests;

public class ProviderConfigShapesTests
{
    [Fact]
    public void AnthropicShapeCanBeConstructed()
    {
        var config = new ProviderConfig(
            BaseUrl: "https://api.anthropic.com",
            AuthScheme: "x-api-key",
            ExtraHeaders: new Dictionary<string, string>
            {
                { "x-api-key", "key_value" },
                { "anthropic-version", "2023-06-01" }
            },
            Vendor: "anthropic",
            DefaultTimeout: TimeSpan.FromSeconds(30)
        );

        Assert.NotNull(config);
        Assert.Equal("anthropic", config.Vendor);
        Assert.Equal("x-api-key", config.AuthScheme);
        Assert.Contains("x-api-key", config.ExtraHeaders.Keys);
    }

    [Fact]
    public void OpenAICompatibleShapeCanBeConstructed()
    {
        var config = new ProviderConfig(
            BaseUrl: "https://api.openai.com",
            AuthScheme: "Bearer",
            ExtraHeaders: new Dictionary<string, string>
            {
                { "Authorization", "Bearer key_value" }
            },
            Vendor: "openai",
            DefaultTimeout: TimeSpan.FromSeconds(30)
        );

        Assert.NotNull(config);
        Assert.Equal("openai", config.Vendor);
        Assert.Equal("Bearer", config.AuthScheme);
        Assert.Contains("Authorization", config.ExtraHeaders.Keys);
    }

    [Fact]
    public void NoAuthLocalShapeCanBeConstructed()
    {
        var config = new ProviderConfig(
            BaseUrl: "http://localhost:11434",
            AuthScheme: "none",
            ExtraHeaders: new Dictionary<string, string>(),
            Vendor: "ollama",
            DefaultTimeout: TimeSpan.FromSeconds(30)
        );

        Assert.NotNull(config);
        Assert.Equal("ollama", config.Vendor);
        Assert.Equal("none", config.AuthScheme);
        Assert.Empty(config.ExtraHeaders);
    }
}
