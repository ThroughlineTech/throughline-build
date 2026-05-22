namespace ThroughlineBuild.Anthropic;

public class AnthropicApiException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public AnthropicApiException(int status, string body)
        : base($"Anthropic API returned {status}: {body}")
    {
        Status = status;
        Body = body;
    }
}
