namespace ThroughlineBuild.Workers.Common;

/// <summary>
/// Public facade over the internal <see cref="MarkdownRenderer"/> so callers outside this
/// assembly (e.g. the CLI's comment verb) can render markdown to Plane-compatible HTML
/// without taking a reflection-based markdown dependency. Same AOT-safe renderer the phases use.
/// </summary>
public static class MarkdownHtml
{
    /// <summary>Render a CommonMark subset to Plane-compatible HTML.</summary>
    public static string Render(string markdown) => MarkdownRenderer.Render(markdown);
}
