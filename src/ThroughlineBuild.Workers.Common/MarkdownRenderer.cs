namespace ThroughlineBuild.Workers.Common;

// Hand-rolled over Markdig: AOT safety - no reflection, no dynamic dispatch,
// standard string operations only. Unsupported constructs (tables, footnotes,
// definition lists, etc.) pass through as literal text.
internal static class MarkdownRenderer
{
    // Renders a CommonMark subset to Plane-compatible HTML.
    internal static string Render(string markdown)
    {
        if (markdown.Length == 0)
            return string.Empty;

        // Normalize line endings to \n.
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var output = new System.Text.StringBuilder();
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block: starts with ```
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                string lang = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;
                i++;
                var codeLines = new System.Text.StringBuilder();
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Append(EscapeHtml(lines[i]));
                    codeLines.Append('\n');
                    i++;
                }
                // Skip closing fence
                if (i < lines.Length)
                    i++;
                string codeContent = codeLines.ToString();
                if (lang.Length > 0)
                    output.Append($"<pre><code class=\"language-{EscapeHtml(lang)}\">{codeContent}</code></pre>");
                else
                    output.Append($"<pre><code>{codeContent}</code></pre>");
                continue;
            }

            // ATX heading: starts with one or more # characters followed by space
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                int level = 0;
                while (level < line.Length && line[level] == '#')
                    level++;
                if (level <= 6 && level < line.Length && line[level] == ' ')
                {
                    string text = line.Substring(level + 1).Trim();
                    output.Append($"<h{level}>{RenderInline(text)}</h{level}>");
                    i++;
                    continue;
                }
            }

            // Unordered or ordered list: collect contiguous list lines
            if (IsUnorderedListItem(line) || IsOrderedListItem(line))
            {
                i = RenderList(lines, i, 0, output);
                continue;
            }

            // Blank line: skip
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            // Paragraph: collect lines until blank line or block element
            var paraLines = new System.Text.StringBuilder();
            while (i < lines.Length)
            {
                var current = lines[i];
                if (current.Trim().Length == 0)
                    break;
                if (current.StartsWith("#", StringComparison.Ordinal))
                    break;
                if (current.StartsWith("```", StringComparison.Ordinal))
                    break;
                if (IsUnorderedListItem(current) || IsOrderedListItem(current))
                    break;
                if (paraLines.Length > 0)
                    paraLines.Append(' ');
                paraLines.Append(current.Trim());
                i++;
            }
            if (paraLines.Length > 0)
                output.Append($"<p>{RenderInline(paraLines.ToString())}</p>");
        }

        return output.ToString();
    }

    // Recursively render a list block starting at lines[startIndex] with baseIndent.
    // Returns the next line index after the list.
    private static int RenderList(string[] lines, int startIndex, int baseIndent, System.Text.StringBuilder output)
    {
        bool isOrdered = IsOrderedListItem(StripIndent(lines[startIndex], baseIndent));
        string openTag = isOrdered ? "<ol>" : "<ul>";
        string closeTag = isOrdered ? "</ol>" : "</ul>";
        output.Append(openTag);

        int i = startIndex;
        while (i < lines.Length)
        {
            var raw = lines[i];
            // Count leading spaces
            int indent = CountIndent(raw);
            if (indent < baseIndent)
                break;

            var stripped = raw.Substring(indent);

            // Check if this is a list item at the current indent level
            if (indent == baseIndent && (IsUnorderedListItem(stripped) || IsOrderedListItem(stripped)))
            {
                string itemText = GetListItemText(stripped);
                output.Append("<li>");
                output.Append(RenderInline(itemText));

                // Look ahead for nested items
                int j = i + 1;
                while (j < lines.Length)
                {
                    var nextRaw = lines[j];
                    if (nextRaw.Trim().Length == 0)
                    {
                        j++;
                        continue;
                    }
                    int nextIndent = CountIndent(nextRaw);
                    var nextStripped = nextRaw.Substring(nextIndent);
                    if (nextIndent > baseIndent && (IsUnorderedListItem(nextStripped) || IsOrderedListItem(nextStripped)))
                    {
                        // Nested list
                        j = RenderList(lines, j, nextIndent, output);
                    }
                    else
                    {
                        break;
                    }
                }
                i = j;
                output.Append("</li>");
                continue;
            }

            // Not a list item at base indent - stop
            break;
        }

        output.Append(closeTag);
        return i;
    }

    private static bool IsUnorderedListItem(string line)
    {
        return (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal));
    }

    private static bool IsOrderedListItem(string line)
    {
        if (line.Length < 3)
            return false;
        int k = 0;
        while (k < line.Length && char.IsDigit(line[k]))
            k++;
        return k > 0 && k < line.Length && line[k] == '.' && k + 1 < line.Length && line[k + 1] == ' ';
    }

    private static string GetListItemText(string line)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal))
            return line.Substring(2);
        // Ordered: skip digits + ". "
        int k = 0;
        while (k < line.Length && char.IsDigit(line[k]))
            k++;
        // skip ". "
        return line.Substring(k + 2);
    }

    private static int CountIndent(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }

    private static string StripIndent(string line, int indent)
    {
        if (indent >= line.Length)
            return string.Empty;
        return line.Substring(indent);
    }

    // Renders inline markdown spans within a block of text.
    // Handles: inline code, bold, italic, links.
    internal static string RenderInline(string text)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // Inline code: backtick
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    string code = text.Substring(i + 1, end - i - 1);
                    sb.Append($"<code>{EscapeHtml(code)}</code>");
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **text**
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    string inner = text.Substring(i + 2, end - i - 2);
                    sb.Append($"<strong>{RenderInline(inner)}</strong>");
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* (single star, not double)
            if (c == '*' && (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                int end = FindSingleStar(text, i + 1);
                if (end > i)
                {
                    string inner = text.Substring(i + 1, end - i - 1);
                    sb.Append($"<em>{RenderInline(inner)}</em>");
                    i = end + 1;
                    continue;
                }
            }

            // Link: [text](url)
            if (c == '[')
            {
                int closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 1)
                    {
                        string linkText = text.Substring(i + 1, closeBracket - i - 1);
                        string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                        sb.Append($"<a href=\"{EscapeHtml(url)}\">{RenderInline(linkText)}</a>");
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            // Escape HTML special chars in literal text
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                default: sb.Append(c); break;
            }
            i++;
        }
        return sb.ToString();
    }

    // Finds the next single star (not part of **) at or after startIndex.
    private static int FindSingleStar(string text, int startIndex)
    {
        int i = startIndex;
        while (i < text.Length)
        {
            if (text[i] == '*')
            {
                // Make sure it's not a double star
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    continue;
                }
                return i;
            }
            i++;
        }
        return -1;
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
