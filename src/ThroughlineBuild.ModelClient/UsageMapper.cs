namespace ThroughlineBuild.ModelClient;

public static class UsageMapper
{
    public static Dictionary<string, object?> ToMetadataDict(Usage usage) =>
        new()
        {
            ["vendor"] = usage.Vendor,
            ["model"] = usage.Model,
            ["input_tokens"] = usage.InputTokens,
            ["output_tokens"] = usage.OutputTokens,
            ["cache_read_tokens"] = (object?)usage.CacheReadTokens ?? 0,
            ["cache_creation_tokens"] = (object?)usage.CacheCreateTokens ?? 0,
            ["cost_usd"] = usage.Cost
        };
}
