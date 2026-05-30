namespace ThroughlineBuild.Plane;

public class PlaneClientOptions
{
    public string BaseUrl { get; init; } = "https://api.plane.so";
    public string ApiToken { get; init; } = string.Empty;
    public string WorkspaceSlug { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectIdentifier { get; init; } = string.Empty;

    /// <summary>
    /// Per-process ceiling on Plane HTTP requests per minute. Every call routes
    /// through a <see cref="RequestThrottle"/> that blocks once this budget is
    /// spent. Plane enforces its real limit (60/min) server-side and globally per
    /// API token, so this throttle is per-process and cannot coordinate across
    /// concurrent <c>build</c> instances. We default to 40 (not 60) to leave
    /// headroom for a second instance sharing the same token, and rely on the
    /// resilience pipeline to back off gracefully when Plane still returns 429.
    /// </summary>
    public int RequestsPerMinute { get; init; } = 40;

    /// <summary>
    /// Maximum retry attempts for transient Plane failures (429 / 5xx) before the
    /// <see cref="PlaneApiException"/> is rethrown. Total HTTP attempts is this + 1.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 5;

    /// <summary>
    /// Base delay for the exponential-with-jitter retry backoff. Used only when a
    /// 429 response carries no <c>Retry-After</c> header; the header takes precedence.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling on any single retry wait, including a server-supplied <c>Retry-After</c>.
    /// Plane's rate-limit window is one minute, so a longer Retry-After is clamped here.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);
}
