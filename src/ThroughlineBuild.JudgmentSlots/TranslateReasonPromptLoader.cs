using System.Reflection;

namespace ThroughlineBuild.JudgmentSlots;

/// <summary>
/// Loads the embedded translate-reason-prompt.md template from this assembly.
/// Keeps the single-binary AOT contract: no disk-relative path lookup.
/// </summary>
public static class TranslateReasonPromptLoader
{
    private static readonly Assembly _assembly = typeof(TranslateReasonPromptLoader).Assembly;
    private static string? _cached;

    /// <summary>
    /// Returns the contents of the embedded translate-reason-prompt.md template.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing
    /// (indicates a build configuration problem).
    /// </summary>
    public static string Load()
    {
        if (_cached is not null)
            return _cached;

        var resourceName = "ThroughlineBuild.JudgmentSlots.Templates.translate-reason-prompt.md";
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
