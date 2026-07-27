using ThroughlineBuild.ModelClient;
using Xunit;

namespace ThroughlineBuild.ModelClient.Tests;

public class UsageMapperTests
{
    [Fact]
    public void ToMetadataDict_ContainsAllSevenKeys()
    {
        var usage = new Usage(
            InputTokens: 100,
            OutputTokens: 50,
            CacheReadTokens: 10,
            CacheCreateTokens: 5,
            Model: "claude-sonnet-4-6",
            Vendor: "anthropic",
            Cost: 0.01m
        );

        var result = UsageMapper.ToMetadataDict(usage);

        Assert.NotNull(result);
        Assert.Equal(7, result.Count);
        Assert.Contains("vendor", result.Keys);
        Assert.Contains("model", result.Keys);
        Assert.Contains("input_tokens", result.Keys);
        Assert.Contains("output_tokens", result.Keys);
        Assert.Contains("cache_read_tokens", result.Keys);
        Assert.Contains("cache_creation_tokens", result.Keys);
        Assert.Contains("cost_usd", result.Keys);
    }

    [Fact]
    public void ToMetadataDict_NullCostMapsToNull()
    {
        var usage = new Usage(
            InputTokens: 100,
            OutputTokens: 50,
            CacheReadTokens: 10,
            CacheCreateTokens: 5,
            Model: "claude-sonnet-4-6",
            Vendor: "anthropic",
            Cost: null
        );

        var result = UsageMapper.ToMetadataDict(usage);

        Assert.Null(result["cost_usd"]);
    }

    [Fact]
    public void ToMetadataDict_NonNullValuesPassThrough()
    {
        var usage = new Usage(
            InputTokens: 1234,
            OutputTokens: 567,
            CacheReadTokens: 100,
            CacheCreateTokens: 50,
            Model: "test-model",
            Vendor: "test-vendor",
            Cost: 0.99m
        );

        var result = UsageMapper.ToMetadataDict(usage);

        Assert.Equal(1234, result["input_tokens"]);
        Assert.Equal(567, result["output_tokens"]);
        Assert.Equal(100, result["cache_read_tokens"]);
        Assert.Equal(50, result["cache_creation_tokens"]);
        Assert.Equal("test-model", result["model"]);
        Assert.Equal("test-vendor", result["vendor"]);
        Assert.Equal(0.99m, result["cost_usd"]);
    }

    [Fact]
    public void ToMetadataDict_NullCacheReadTokensMapsToZero()
    {
        var usage = new Usage(
            InputTokens: 100,
            OutputTokens: 50,
            CacheReadTokens: null,
            CacheCreateTokens: 5,
            Model: "claude-sonnet-4-6",
            Vendor: "anthropic",
            Cost: 0.01m
        );

        var result = UsageMapper.ToMetadataDict(usage);

        Assert.Equal(0, result["cache_read_tokens"]);
    }

    [Fact]
    public void ToMetadataDict_NullCacheCreateTokensMapsToZero()
    {
        var usage = new Usage(
            InputTokens: 100,
            OutputTokens: 50,
            CacheReadTokens: 10,
            CacheCreateTokens: null,
            Model: "claude-sonnet-4-6",
            Vendor: "anthropic",
            Cost: 0.01m
        );

        var result = UsageMapper.ToMetadataDict(usage);

        Assert.Equal(0, result["cache_creation_tokens"]);
    }
}
