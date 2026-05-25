using System.Text.RegularExpressions;

namespace ThroughlineBuild.Briefs;

public static class TemplateExtensions
{
    private static readonly Regex _pattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Substitute(this string template, IReadOnlyDictionary<string, string> variables)
    {
        return _pattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!variables.TryGetValue(key, out var value))
                throw new InvalidOperationException($"Template variable '{key}' is missing from the substitution dictionary.");
            return value;
        });
    }
}
