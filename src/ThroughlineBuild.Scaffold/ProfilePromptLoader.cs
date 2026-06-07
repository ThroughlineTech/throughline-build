using System.Reflection;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Loads the embedded derive-profile-prompt.md template from this assembly.
/// Keeps the single-binary AOT contract: no disk-relative path lookup.
/// </summary>
public static class ProfilePromptLoader
{
    private const string ResourceName = "ThroughlineBuild.Scaffold.Templates.derive-profile-prompt.md";
    private static readonly Assembly Assembly = typeof(ProfilePromptLoader).Assembly;
    private static string? _cached;

    /// <summary>
    /// Returns the raw derive-profile-prompt.md template, including its
    /// <c>{{op_doc_markdown}}</c> placeholder. Callers substitute the placeholder.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing
    /// (indicates a build configuration problem).
    /// </summary>
    public static string Load()
    {
        if (_cached is not null)
            return _cached;

        using var stream = Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            var available = string.Join(", ", Assembly.GetManifestResourceNames().OrderBy(name => name));
            throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. Available resources: {available}");
        }

        using var reader = new StreamReader(stream);
        _cached = reader.ReadToEnd();
        return _cached;
    }
}
