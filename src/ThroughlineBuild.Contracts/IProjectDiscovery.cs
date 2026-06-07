namespace ThroughlineBuild.Contracts;

/// <summary>
/// Summarizes one workspace project as returned by <see cref="IProjectDiscovery.ListProjectsAsync"/>.
/// </summary>
public readonly record struct ProjectInfo(string Id, string Name, string Identifier);

/// <summary>
/// Workspace-level project discovery and creation. These operations route on the workspace
/// slug alone and do not require a configured project id; they are used at bootstrap time to
/// turn a project name into the uuid the rest of the system needs.
/// </summary>
public interface IProjectDiscovery
{
    /// <summary>Lists all projects in the workspace, returning id, name, and identifier for each.</summary>
    Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the project id for the project whose name matches <paramref name="name"/>
    /// (case-insensitive), or null if no matching project is found.
    /// </summary>
    Task<string?> FindProjectByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Creates a new project with the given name and Plane identifier prefix, and returns the
    /// new project's id. A 401 or 403 from Plane surfaces as a typed exception with a message
    /// that names the missing workspace-admin scope.
    /// </summary>
    Task<string> CreateProjectAsync(string name, string identifier, CancellationToken ct);
}
