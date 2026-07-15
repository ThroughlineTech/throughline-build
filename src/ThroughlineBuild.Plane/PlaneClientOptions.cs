namespace ThroughlineBuild.Plane;

public class PlaneClientOptions
{
    public string BaseUrl { get; init; } = "https://api.plane.so";
    public string ApiToken { get; init; } = string.Empty;
    public string WorkspaceSlug { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectIdentifier { get; init; } = string.Empty;

    /// <summary>
    /// Default per-minute request budget: sized for Plane Cloud, whose real limit
    /// is 60/min server-side and global per API token. We sit at 40 (not 60) to
    /// leave headroom for a second instance sharing the same token.
    /// </summary>
    public const int DefaultRequestsPerMinute = 40;

    /// <summary>
    /// Per-process ceiling on Plane HTTP requests per minute. Every call routes
    /// through a <see cref="RequestThrottle"/> that blocks once this budget is
    /// spent. The throttle is per-process and cannot coordinate across concurrent
    /// <c>build</c> instances, so the resilience pipeline still has to back off
    /// gracefully when Plane returns 429 anyway.
    ///
    /// The default is calibrated to Plane Cloud. A self-hosted Plane sets its own
    /// limit (or none), so operators of a self-hosted instance can raise this via
    /// <c>plane_requests_per_minute</c> in the <c>[ticketing]</c> block of
    /// <c>.build/config.toml</c> (TLB-565). Nothing here queries the server for its
    /// real limit; this is a self-imposed budget only.
    /// </summary>
    public int RequestsPerMinute { get; init; } = DefaultRequestsPerMinute;

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

    /// <summary>
    /// Retries for transient TRANSPORT failures - DNS resolution, connect, TLS handshake,
    /// timeout - where no HTTP status exists yet (distinct from <see cref="MaxRetryAttempts"/>,
    /// which governs 429/5xx responses). When exhausted the failure surfaces as a
    /// <see cref="ThroughlineBuild.Contracts.TicketingUnavailableException"/> so orchestration
    /// can classify it as environmental instead of crashing the run (TLB-545). 0 disables.
    /// </summary>
    public int TransportRetryAttempts { get; init; } = 3;

    /// <summary>Base delay for the transport-retry exponential backoff (doubled per attempt, jittered).</summary>
    public TimeSpan TransportRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on a single transport-retry wait. Transient DNS/connect blips clear in seconds; anything longer is an outage the chain should classify, not sit out.</summary>
    public TimeSpan TransportMaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
}
