using System.Reflection;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Loads the embedded op-doc authoring spec from this assembly.
/// Keeps the spec version-locked beside the parser and validator that enforce it.
/// </summary>
public static class OpDocSpecLoader
{
    private static readonly Assembly _assembly = typeof(OpDocSpecLoader).Assembly;
    private static readonly string _assemblyName = _assembly.GetName().Name!;
    private static string? _cached;

    /// <summary>
    /// Returns the contents of the embedded op-doc-spec.md template.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing
    /// (indicates a build configuration problem).
    /// </summary>
    public static string Load()
    {
        if (_cached is not null)
            return _cached;

        var resourceName = $"{_assemblyName}.Templates.op-doc-spec.md";
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
