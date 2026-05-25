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

    public static string Load(string templateName)
    {
        return _cache.GetOrAdd(templateName, name =>
        {
            var resourceName = $"{_assemblyName}.Templates.{name}";
            using var stream = _assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var available = string.Join(", ", _allNames.Value.Order());
                throw new InvalidOperationException(
                    $"Template '{name}' not found. Available resources: {available}");
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
