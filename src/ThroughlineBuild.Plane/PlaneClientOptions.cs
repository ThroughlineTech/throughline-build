namespace ThroughlineBuild.Plane;

public class PlaneClientOptions
{
    public string BaseUrl { get; init; } = "https://api.plane.so";
    public string ApiToken { get; init; } = string.Empty;
    public string WorkspaceSlug { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectIdentifier { get; init; } = string.Empty;

    /// <summary>
    /// Hard ceiling on Plane HTTP requests per minute. Every call routes through
    /// a shared <see cref="RequestThrottle"/> that blocks once this budget is
    /// spent, so the client never trips Plane's 429 rate limit. Plane's published
    /// limit is 60/min.
    /// </summary>
    public int RequestsPerMinute { get; init; } = 60;
}
