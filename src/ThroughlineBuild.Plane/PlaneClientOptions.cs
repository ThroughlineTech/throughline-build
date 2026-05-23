namespace ThroughlineBuild.Plane;

public class PlaneClientOptions
{
    public string BaseUrl { get; init; } = "https://api.plane.so";
    public string ApiToken { get; init; } = string.Empty;
    public string WorkspaceSlug { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectIdentifier { get; init; } = string.Empty;
}
