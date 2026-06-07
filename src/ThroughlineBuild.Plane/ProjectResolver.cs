using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Plane;

/// <summary>
/// Resolves a project name to a Plane project id, creating the project if it does not yet
/// exist, and reports which path was taken.
///
/// The primary constructor accepts raw credentials (base URL, workspace slug, API token) and
/// builds a <see cref="PlaneTicketingClient"/> internally, so the resolver can run before any
/// .build/config.toml has been written to disk.
/// </summary>
public sealed class ProjectResolver : IProjectResolver
{
    private readonly IProjectDiscovery _discovery;

    /// <summary>
    /// Constructs a resolver from raw Plane credentials. The caller owns the
    /// <see cref="HttpClient"/> lifetime; inject a test double via the other constructor in
    /// unit tests.
    /// </summary>
    /// <param name="http">The HTTP client used for all Plane API calls.</param>
    /// <param name="baseUrl">Plane base URL, e.g. "https://api.plane.so".</param>
    /// <param name="workspaceSlug">Plane workspace slug.</param>
    /// <param name="apiToken">Plane API token with at least workspace-member scope (workspace-admin to create projects).</param>
    public ProjectResolver(HttpClient http, string baseUrl, string workspaceSlug, string apiToken)
        : this(new PlaneTicketingClient(http, new PlaneClientOptions
        {
            BaseUrl = baseUrl,
            WorkspaceSlug = workspaceSlug,
            ApiToken = apiToken,
        }))
    {
    }

    /// <summary>
    /// Constructs a resolver from an existing <see cref="IProjectDiscovery"/> instance.
    /// Use this constructor to inject a test double.
    /// </summary>
    public ProjectResolver(IProjectDiscovery discovery)
    {
        _discovery = discovery;
    }

    /// <inheritdoc />
    public async Task<ProjectResolveResult> ResolveAsync(string name, CancellationToken ct)
    {
        var existingId = await _discovery.FindProjectByNameAsync(name, ct).ConfigureAwait(false);
        if (existingId is not null)
            return new ProjectResolveResult(existingId, ProjectResolveOutcome.Found);

        var identifier = DeriveIdentifier(name);
        var newId = await _discovery.CreateProjectAsync(name, identifier, ct).ConfigureAwait(false);
        return new ProjectResolveResult(newId, ProjectResolveOutcome.Created);
    }

    /// <summary>
    /// Derives a short uppercase Plane identifier (2-10 chars) from a project name.
    /// Uses first-letter initials for multi-word names and the first six letters for single-word
    /// names. Plane requires identifiers to be uppercase letters only. Public so the interactive
    /// init flow can offer this as the default identifier the operator can accept or override.
    /// </summary>
    public static string DeriveIdentifier(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 1)
        {
            var letters = new string(name.Where(char.IsLetter).ToArray());
            var take = Math.Min(6, letters.Length);
            return take >= 2 ? letters[..take].ToUpperInvariant() : "PROJ";
        }

        var initials = new string(words
            .Select(w => w.FirstOrDefault(char.IsLetter))
            .Where(c => c != '\0')
            .ToArray());

        var trimmed = initials[..Math.Min(10, initials.Length)].ToUpperInvariant();
        return trimmed.Length >= 2 ? trimmed : "PROJ";
    }
}
