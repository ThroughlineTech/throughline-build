using System.Collections.Concurrent;
using System.Reflection;

namespace ThroughlineBuild.Briefs;

public static class TemplateLoader
{
    private static readonly Assembly _assembly = typeof(TemplateLoader).Assembly;
    private static readonly string _assemblyName = _assembly.GetName().Name!;
    private static readonly Lazy<HashSet<string>> _allNames =
        new(() => new HashSet<string>(_assembly.GetManifestResourceNames()));
    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private static readonly Lazy<IReadOnlyList<string>> _agentNames = new(DiscoverAgentNames);

    /// <summary>
    /// Agent names that ship a brief template set, derived from the embedded resource names
    /// rather than a hardcoded list, so a new Templates/&lt;agent&gt;/ directory is picked up by
    /// building. An agent qualifies when it has an implement.md template.
    /// </summary>
    /// <remarks>
    /// MSBuild folds hyphens in directory names to underscores (see <see cref="Load"/>), so this
    /// reverses that mapping. A template directory whose real name contains an underscore would
    /// therefore be reported with a hyphen and would not round-trip; none ship, and such a name
    /// simply fails the membership check rather than loading the wrong template.
    /// </remarks>
    public static IReadOnlyList<string> AvailableAgents() => _agentNames.Value;

    /// <summary>
    /// True when <paramref name="agentName"/> names a shipped brief template set.
    /// Callers validating operator-supplied agent names should use this rather than
    /// catching the load failure, which carries a full resource dump.
    /// </summary>
    public static bool HasTemplates(string agentName) =>
        _agentNames.Value.Contains(agentName, StringComparer.Ordinal);

    private static IReadOnlyList<string> DiscoverAgentNames()
    {
        var prefix = $"{_assemblyName}.Templates.";
        const string suffix = ".implement.md";
        var names = new List<string>();
        foreach (var resourceName in _allNames.Value)
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var segment = resourceName[prefix.Length..^suffix.Length];
            // Exactly one directory segment: nested or top-level matches are not agent directories.
            if (segment.Length == 0 || segment.Contains('.', StringComparison.Ordinal) || segment == "shared")
                continue;

            names.Add(segment.Replace('_', '-'));
        }

        names.Sort(StringComparer.Ordinal);
        return names.AsReadOnly();
    }

    public static string Load(string agentName, string templateName)
    {
        var cacheKey = $"{agentName}.{templateName}";
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            // MSBuild converts hyphens to underscores in embedded resource names when subdirectory
            // names contain hyphens (e.g. "claude-code" becomes "claude_code" in the resource path).
            var resourceSegment = agentName.Replace('-', '_');
            var resourceName = $"{_assemblyName}.Templates.{resourceSegment}.{templateName}";
            using var stream = _assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var available = string.Join(", ", _allNames.Value.Order());
                throw new InvalidOperationException(
                    $"Template '{templateName}' for agent '{agentName}' not found. Available resources: {available}");
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    // Loads an agent-agnostic section template from Templates/shared/. These hold the reusable
    // prose blocks and WORKER_RESULT envelope shapes that the per-agent builders share, so each
    // piece of prompt text lives in exactly one file. The "shared" subdir has no hyphens; file
    // names may contain hyphens, which MSBuild preserves in the resource name (only subdirectory
    // segments are converted to underscores - see Load above).
    public static string LoadShared(string name)
    {
        var cacheKey = $"shared.{name}";
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            var resourceName = $"{_assemblyName}.Templates.shared.{name}";
            using var stream = _assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var available = string.Join(", ", _allNames.Value.Order());
                throw new InvalidOperationException(
                    $"Shared template '{name}' not found. Available resources: {available}");
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
