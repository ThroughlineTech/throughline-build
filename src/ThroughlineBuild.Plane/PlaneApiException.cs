namespace ThroughlineBuild.Plane;

public class PlaneApiException : Exception
{
    public int Status { get; }
    public string Body { get; }

    public PlaneApiException(int status, string body)
        : base($"Plane API returned {status}: {body}")
    {
        Status = status;
        Body = body;
    }
}
