using System.Reflection;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Loads the embedded config.toml.template from this assembly.
/// Keeps the single-binary AOT contract: no disk-relative path lookup.
/// </summary>
public static class ConfigTemplateLoader
{
    private static readonly Assembly _assembly = typeof(ConfigTemplateLoader).Assembly;
    private static readonly string _assemblyName = _assembly.GetName().Name!;
    private static string? _cached;

    /// <summary>
    /// Returns the contents of the embedded config.toml.template.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing
    /// (indicates a build configuration problem).
    /// </summary>
    public static string Load()
    {
        if (_cached is not null)
            return _cached;

        var resourceName = $"{_assemblyName}.Templates.config.toml.template";
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", _assembly.GetManifestResourceNames().OrderBy(n => n));
            throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Available resources: {available}");
        }
        using var reader = new StreamReader(stream);
        _cached = reader.ReadToEnd();
        return _cached;
    }
}
