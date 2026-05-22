using System.Text.RegularExpressions;

namespace ThroughlineBuild.Helpers;

public static class MarkerParser
{
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MarkerPattern = new(@"\[([a-zA-Z_][a-zA-Z0-9_]*?)(?:\s*:\s*([^\]]+?))?\]", RegexOptions.Compiled);

    public static IReadOnlyList<Marker> Parse(string? commentText)
    {
        if (string.IsNullOrEmpty(commentText))
        {
            return new List<Marker>();
        }

        // Strip HTML tags
        var stripped = HtmlTagPattern.Replace(commentText, "");

        // Find all markers
        var markers = new List<Marker>();
        var matches = MarkerPattern.Matches(stripped);

        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var valueGroup = match.Groups[2];
            var value = valueGroup.Success ? valueGroup.Value.Trim() : null;

            markers.Add(new Marker(name, value));
        }

        return markers.AsReadOnly();
    }
}

public record Marker(string Name, string? Value);
