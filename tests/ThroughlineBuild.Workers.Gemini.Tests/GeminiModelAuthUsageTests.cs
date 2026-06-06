using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Gemini;
using Xunit;

namespace ThroughlineBuild.Workers.Gemini.Tests;

public class GeminiModelAuthUsageTests
{
    // ---------------------------------------------------------------------------
    // NormalizeModel
    // ---------------------------------------------------------------------------

    [Fact]
    public void NormalizeModel_StripsPrefixGoogle()
    {
        var result = GeminiAgent.NormalizeModel("google:gemini-2.5-pro");
        Assert.Equal("gemini-2.5-pro", result);
    }

    [Fact]
    public void NormalizeModel_StripsPrefixGoogleCaseInsensitive()
    {
        var result = GeminiAgent.NormalizeModel("GOOGLE:gemini-2.0-flash");
        Assert.Equal("gemini-2.0-flash", result);
    }

    [Fact]
    public void NormalizeModel_NoPrefix_ReturnsAsIs()
    {
        var result = GeminiAgent.NormalizeModel("gemini-2.5-flash");
        Assert.Equal("gemini-2.5-flash", result);
    }

    [Fact]
    public void NormalizeModel_NullInput_ReturnsNull()
    {
        var result = GeminiAgent.NormalizeModel(null);
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeModel_EmptyString_ReturnsNull()
    {
        var result = GeminiAgent.NormalizeModel("");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeModel_PrefixOnly_ReturnsNull()
    {
        var result = GeminiAgent.NormalizeModel("google:");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeModel_WhitespaceOnly_ReturnsNull()
    {
        var result = GeminiAgent.NormalizeModel("   ");
        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------
    // ConfigureEnvironment - strips API key env vars
    // ---------------------------------------------------------------------------

    [Fact]
    public void ConfigureEnvironment_RemovesGeminiApiKey()
    {
        var agent = new GeminiAgent();
        var psi = new ProcessStartInfo("dummy");
        psi.Environment["GEMINI_API_KEY"] = "secret-key";

        agent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromMinutes(1)));

        Assert.False(psi.Environment.ContainsKey("GEMINI_API_KEY"));
    }

    [Fact]
    public void ConfigureEnvironment_RemovesGoogleApiKey()
    {
        var agent = new GeminiAgent();
        var psi = new ProcessStartInfo("dummy");
        psi.Environment["GOOGLE_API_KEY"] = "another-secret";

        agent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromMinutes(1)));

        Assert.False(psi.Environment.ContainsKey("GOOGLE_API_KEY"));
    }

    [Fact]
    public void ConfigureEnvironment_RemovesBothApiKeyVarsWhenBothPresent()
    {
        var agent = new GeminiAgent();
        var psi = new ProcessStartInfo("dummy");
        psi.Environment["GEMINI_API_KEY"] = "key1";
        psi.Environment["GOOGLE_API_KEY"] = "key2";

        agent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromMinutes(1)));

        Assert.False(psi.Environment.ContainsKey("GEMINI_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("GOOGLE_API_KEY"));
    }

    [Fact]
    public void ConfigureEnvironment_AppliesUserEnvironmentVariables()
    {
        var agent = new GeminiAgent();
        var psi = new ProcessStartInfo("dummy");
        var options = new WorkerOptions(
            TimeSpan.FromMinutes(1),
            EnvironmentVariables: new Dictionary<string, string>
            {
                { "MY_CUSTOM_VAR", "hello" }
            });

        agent.ConfigureEnvironment(psi, options);

        Assert.Equal("hello", psi.Environment["MY_CUSTOM_VAR"]);
    }

    // ---------------------------------------------------------------------------
    // BuildLlmUsageMetadata
    // ---------------------------------------------------------------------------

    [Fact]
    public void BuildLlmUsageMetadata_VendorIsGoogle()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 100, "gemini-2.5-pro");
        Assert.Equal("google", result["vendor"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_CostUsdIsNull()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 100, "gemini-2.5-pro");
        Assert.True(result.ContainsKey("cost_usd"));
        Assert.Null(result["cost_usd"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_CarriesModel()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 100, "gemini-2.5-pro");
        Assert.Equal("gemini-2.5-pro", result["model"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_NullModel_CarriesEmptyString()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 100, null);
        Assert.Equal("", result["model"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithUsage_InputTokensFromTotal()
    {
        var usage = new GeminiUsage(new GeminiTokenStats(42), null);
        var result = GeminiAgent.BuildLlmUsageMetadata(usage, 500, "gemini-2.0-flash");
        Assert.Equal(42, result["input_tokens"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_NullUsage_InputTokensZero()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 500, "gemini-2.0-flash");
        Assert.Equal(0, result["input_tokens"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_OutputTokensIsZero()
    {
        var usage = new GeminiUsage(new GeminiTokenStats(99), null);
        var result = GeminiAgent.BuildLlmUsageMetadata(usage, 500, "gemini-2.0-flash");
        Assert.Equal(0, result["output_tokens"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WallClockMsIsPresent()
    {
        var result = GeminiAgent.BuildLlmUsageMetadata(null, 1234, "gemini-2.5-flash");
        Assert.Equal(1234L, result["wall_clock_ms"]);
    }

    // ---------------------------------------------------------------------------
    // GeminiOptions default sizes
    // ---------------------------------------------------------------------------

    [Fact]
    public void GeminiOptions_DefaultSizes_IsEmpty()
    {
        // The dead in-code default size map was removed; models come from
        // [workers.gemini.sizes] in config. Default Sizes is empty.
        var options = new GeminiOptions();
        Assert.Empty(options.Sizes);
    }

    [Fact]
    public void GeminiOptions_DefaultSizes_IsEmptyCount()
    {
        // The dead in-code default size map was removed; default Sizes is empty.
        var options = new GeminiOptions();
        Assert.Empty(options.Sizes);
    }
}
