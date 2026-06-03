using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexModelAuthUsageTests
{
    [Theory]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("openai:gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("openai:gpt-5.3-codex", "gpt-5.3-codex")]
    [InlineData("OPENAI:gpt-5.5", "gpt-5.5")]
    public void NormalizeModel_StripsPrefix(string input, string expected)
    {
        Assert.Equal(expected, CodexAgent.NormalizeModel(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("openai:")]
    public void NormalizeModel_ReturnsNull_ForEmptyOrPrefixOnly(string? input)
    {
        Assert.Null(CodexAgent.NormalizeModel(input));
    }

    [Fact]
    public void ConfigureEnvironment_RemovesApiKeyVars()
    {
        var options = new CodexOptions();
        var agent = new CodexAgent(options);
        var psi = new ProcessStartInfo("codex");
        psi.Environment["CODEX_API_KEY"] = "secret1";
        psi.Environment["OPENAI_API_KEY"] = "secret2";
        var workerOptions = new WorkerOptions(Timeout: TimeSpan.FromMinutes(5));

        agent.ConfigureEnvironment(psi, workerOptions);

        Assert.False(psi.Environment.ContainsKey("CODEX_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_HasCorrectVendorAndNullCost()
    {
        var meta = CodexAgent.BuildLlmUsageMetadata(100, 200, 5000, "gpt-5.3-codex");

        Assert.Equal("openai", meta["vendor"]);
        Assert.Equal("gpt-5.3-codex", meta["model"]);
        Assert.Null(meta["cost_usd"]);
        Assert.Equal(100, meta["input_tokens"]);
        Assert.Equal(200, meta["output_tokens"]);
        Assert.Equal(5000L, meta["wall_clock_ms"]);
    }

    [Fact]
    public void CodexOptions_DefaultSizes_HaveAllThreeTiers()
    {
        var opts = new CodexOptions();
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Small));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Medium));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Large));
    }

    [Fact]
    public void CodexOptions_DefaultSizes_MapToCurrentOpenAiTiers()
    {
        var opts = new CodexOptions();
        Assert.Equal("gpt-5.4-mini", opts.Sizes[WorkerSize.Small]);
        Assert.Equal("gpt-5.3-codex", opts.Sizes[WorkerSize.Medium]);
        Assert.Equal("gpt-5.5", opts.Sizes[WorkerSize.Large]);
    }
}
