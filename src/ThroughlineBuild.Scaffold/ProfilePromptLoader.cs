using System.Reflection;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Loads the shared profile derivation rules together with a source-specific wrapper.
/// Keeps the single-binary AOT contract: no disk-relative path lookup.
/// </summary>
public static class ProfilePromptLoader
{
    private const string OpDocResourceName = "ThroughlineBuild.Scaffold.Templates.derive-profile-prompt.md";
    private const string RepositoryResourceName = "ThroughlineBuild.Scaffold.Templates.derive-profile-repository-prompt.md";
    private const string RulesResourceName = "ThroughlineBuild.Scaffold.Templates.derive-profile-rules.md";
    private static readonly Assembly Assembly = typeof(ProfilePromptLoader).Assembly;
    private static string? _opDocCached;
    private static string? _repositoryCached;
    private static string? _rulesCached;

    /// <summary>
    /// Returns the op-doc derivation prompt, including its
    /// <c>{{op_doc_markdown}}</c> placeholder. Callers substitute the placeholder.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing
    /// (indicates a build configuration problem).
    /// </summary>
    public static string Load()
    {
        if (_opDocCached is not null)
            return _opDocCached;

        _opDocCached = LoadResource(OpDocResourceName).Replace("{{profile_rules}}", LoadRules());
        return _opDocCached;
    }

    /// <summary>
    /// Returns the repository-interrogation prompt. Its response contract is a single JSON object,
    /// so an interactive agent can pipe its result directly to <c>build profile apply -</c>.
    /// </summary>
    public static string LoadRepository()
    {
        if (_repositoryCached is not null)
            return _repositoryCached;

        _repositoryCached = LoadResource(RepositoryResourceName).Replace("{{profile_rules}}", LoadRules());
        return _repositoryCached;
    }

    private static string LoadRules()
    {
        if (_rulesCached is not null)
            return _rulesCached;

        _rulesCached = LoadResource(RulesResourceName);
        return _rulesCached;
    }

    private static string LoadResource(string resourceName)
    {
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = string.Join(", ", Assembly.GetManifestResourceNames().OrderBy(name => name));
            throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Available resources: {available}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
