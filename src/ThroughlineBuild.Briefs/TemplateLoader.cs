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
}
