namespace ThroughlineBuild.Contracts;

/// <summary>
/// Outcome of a project resolution attempt.
/// </summary>
public enum ProjectResolveOutcome
{
    /// <summary>The project already existed and its id was returned.</summary>
    Found,

    /// <summary>The project did not exist and was created; its new id is returned.</summary>
    Created,
}

/// <summary>
/// Result returned by <see cref="IProjectResolver.ResolveAsync"/>.
/// Carries both the project id and whether that id came from an existing project or a new one,
/// so the caller can report the distinction to the operator.
/// </summary>
public readonly record struct ProjectResolveResult(string ProjectId, ProjectResolveOutcome Outcome);

/// <summary>
/// Turns a project name into a concrete Plane project id, finding the project if it already
/// exists or creating it if not, and reports which path was taken. Designed to run before any
/// .build/config.toml has been written - the implementation must build its Plane client from
/// raw credentials rather than from a loaded config.
/// </summary>
public interface IProjectResolver
{
    /// <summary>
    /// Returns the id of the workspace project whose name matches <paramref name="name"/>
    /// (case-insensitive) if one already exists, or creates a new project with that name and
    /// returns its new id. The <see cref="ProjectResolveResult.Outcome"/> field distinguishes
    /// the two paths so the caller can report "found" vs "created" to the operator.
    /// </summary>
    Task<ProjectResolveResult> ResolveAsync(string name, CancellationToken ct);
}
