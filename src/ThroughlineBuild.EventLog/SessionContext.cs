namespace ThroughlineBuild.EventLog;

public record SessionContext(
    string? ProjectId,
    string? ProjectName,
    string? WorkspaceSlug,
    string? BuildVersion);
