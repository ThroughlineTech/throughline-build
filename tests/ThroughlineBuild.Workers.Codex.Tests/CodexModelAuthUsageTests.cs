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
    [InlineData("openai:gpt-5.4", "gpt-5.4")]
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
        var meta = CodexAgent.BuildLlmUsageMetadata(
            inputTokens: 100,
            outputTokens: 200,
            wallClockMs: 5000,
            model: "gpt-5.4",
            cachedInputTokens: 25,
            reasoningOutputTokens: 10);

        Assert.Equal("openai", meta["vendor"]);
        Assert.Equal("gpt-5.4", meta["model"]);
        Assert.Null(meta["cost_usd"]);
        Assert.Equal(100, meta["input_tokens"]);
        Assert.Equal(200, meta["output_tokens"]);
        Assert.Equal(25, meta["cache_read_tokens"]);
        Assert.Equal(0, meta["cache_create_tokens"]);
        Assert.Equal(25, meta["cached_input_tokens"]);
        Assert.Equal(10, meta["reasoning_output_tokens"]);
        Assert.Equal(5000L, meta["wall_clock_ms"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithEffort_CarriesReasoningEffort()
    {
        var meta = CodexAgent.BuildLlmUsageMetadata(
            inputTokens: 100,
            outputTokens: 200,
            wallClockMs: 5000,
            model: "gpt-5.5",
            cachedInputTokens: 25,
            reasoningOutputTokens: 10,
            reasoningEffort: "xhigh");

        Assert.Equal("xhigh", meta["reasoning_effort"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithoutEffort_ReasoningEffortNull()
    {
        var meta = CodexAgent.BuildLlmUsageMetadata(
            inputTokens: 100,
            outputTokens: 200,
            wallClockMs: 5000,
            model: "gpt-5.5");

        Assert.Null(meta["reasoning_effort"]);
    }

    [Fact]
    public void CodexOptions_DefaultSizes_IsEmpty()
    {
        // The dead in-code default size map was removed; models come from
        // [workers.codex.sizes] in config. Default Sizes is empty.
        var opts = new CodexOptions();
        Assert.Empty(opts.Sizes);
    }
}
