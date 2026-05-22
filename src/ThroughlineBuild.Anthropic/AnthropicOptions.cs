namespace ThroughlineBuild.Anthropic;

public class AnthropicOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string ApiVersion { get; init; } = "2023-06-01";
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
}
