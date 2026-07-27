using System.Text;

namespace ThroughlineBuild.Cli.Json;

/// <summary>
/// Renders Plane's stored comment HTML back to readable plain text for display (build comments,
/// text and --json). We store HTML so the Plane web UI renders comments richly, but the terminal
/// and an agent reading the envelope want text, not <c>&lt;p&gt;sample&lt;/p&gt;</c>.
/// Hand-rolled char scan + WebUtility entity decode - no regex, no reflection, AOT-safe.
/// Block elements become line breaks and list items get a "- " marker; inline tags are dropped.
/// This is lossy by design (bold/links/code markers are not recovered) - fine for a discussion log.
/// </summary>
public static class HtmlToText
{
    public static string Render(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var sb = new StringBuilder(html!.Length);
        int i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c == '<')
            {
                var end = html.IndexOf('>', i + 1);
                if (end < 0)
                    break; // malformed tail; drop it
                AppendTagBreak(sb, html.Substring(i + 1, end - i - 1).Trim());
                i = end + 1;
                continue;
            }
            sb.Append(c);
            i++;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(sb.ToString());
        return Normalize(decoded);
    }

    private static void AppendTagBreak(StringBuilder sb, string tag)
    {
        var isClose = tag.StartsWith("/", StringComparison.Ordinal);
        // tag content like "p", "/p", "br", "br /", "li class=...", "h2 data-id=..."
        var name = tag.TrimStart('/').Split(new[] { ' ', '/', '\t', '\n' }, 2)[0].ToLowerInvariant();
        switch (name)
        {
            case "br":
                sb.Append('\n');
                break;
            case "li":
                // Each item on its own line; the close adds nothing so items stay single-spaced.
                if (!isClose)
                    sb.Append("\n- ");
                break;
            case "p":
            case "div":
            case "ul":
            case "ol":
            case "blockquote":
            case "tr":
            case "pre":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                // Block elements are separated by a blank line; Normalize collapses any excess.
                if (isClose)
                    sb.Append("\n\n");
                break;
            default:
                // Inline tags (strong/em/code/a/span/...) contribute no break.
                break;
        }
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var raw in lines)
        {
            sb.Append(CollapseSpaces(raw.Trim()));
            sb.Append('\n');
        }

        var result = sb.ToString();
        while (result.Contains("\n\n\n", StringComparison.Ordinal))
            result = result.Replace("\n\n\n", "\n\n");
        return result.Trim();
    }

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var c in s)
        {
            var isSpace = c == ' ' || c == '\t';
            if (isSpace)
            {
                if (!prevSpace)
                    sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
            prevSpace = isSpace;
        }
        return sb.ToString();
    }
}
