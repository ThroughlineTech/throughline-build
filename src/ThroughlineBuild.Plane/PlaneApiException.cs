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
        : base($"Plane API returned {status}: {body}")
    {
        Status = status;
        Body = body;
        RetryAfter = retryAfter;
    }
}
