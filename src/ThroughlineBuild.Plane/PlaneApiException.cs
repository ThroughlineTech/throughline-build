namespace ThroughlineBuild.Plane;

public class PlaneApiException : Exception
{
    public int Status { get; }
    public string Body { get; }

    /// <summary>
    /// Server-supplied back-off hint parsed from the <c>Retry-After</c> response
    /// header (seconds delta or HTTP-date converted to a delta), when present.
    /// The resilience pipeline honors this over its own exponential backoff so a
    /// 429 waits exactly as long as Plane asks. Null when the response carried no
    /// usable Retry-After header.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public PlaneApiException(int status, string body, TimeSpan? retryAfter = null)
        : this(status, body, $"Plane API returned {status}: {body}", retryAfter)
    {
    }

    /// <summary>
    /// Overload that sets an actionable <see cref="Exception.Message"/> while preserving the raw
    /// <paramref name="status"/> and <paramref name="body"/> for programmatic inspection. Used to
    /// translate an opaque 404 ("Page not found.") into a message that names the likely cause (wrong
    /// workspace/project, or a Plane feature not enabled) at the layer that knows the route, so every
    /// caller that surfaces <c>ex.Message</c> reports the same remedy.
    /// </summary>
    public PlaneApiException(int status, string body, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Status = status;
        Body = body;
        RetryAfter = retryAfter;
    }
}
