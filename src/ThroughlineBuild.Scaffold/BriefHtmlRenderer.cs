using System.Text;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Minimal markdown-subset to HTML renderer for op-doc brief and plan content.
/// Handles: paragraphs, bullet lists (- and *), inline code (`...`), and section headings (h3).
/// Op-doc format restricts brief content to this subset; no full markdown parser is needed.
/// </summary>
public static class BriefHtmlRenderer
{
    /// <summary>
    /// Render an operation's description as HTML for the op-ticket body.
    /// Includes the operation why/rationale.
    /// </summary>
    public static string RenderOpDoc(OpDoc opDoc)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(opDoc.Why))
        {
            sb.Append("<h3>Why</h3>");
            sb.Append(RenderInlineMarkdown(opDoc.Why.Trim()));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Render a plan's description as HTML for the plan-ticket body.
    /// Includes the plan goal and a summary list of briefs in the plan.
    /// </summary>
    public static string RenderPlan(Plan plan)
    {
        var sb = new StringBuilder();

        sb.Append("<h3>Goal</h3>");
        sb.Append(RenderInlineMarkdown(plan.Goal.Trim()));

        if (plan.Briefs.Count > 0)
        {
            sb.Append("<h3>Briefs</h3><ul>");
            foreach (var brief in plan.Briefs)
            {
                sb.Append("<li>");
                sb.Append($"<strong>{EscapeHtml(brief.Slug)}</strong>");
                if (!string.IsNullOrWhiteSpace(brief.Title) && brief.Title != brief.Slug)
                {
                    sb.Append($" - {EscapeHtml(brief.Title)}");
                }
                sb.Append("</li>");
            }
            sb.Append("</ul>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Render a brief's full content as HTML for the brief-ticket body.
    /// Includes Goal, Inputs, Outputs, Acceptance, Notes, and OOS sections.
    /// </summary>
    public static string RenderBrief(Brief brief)
    {
        var sb = new StringBuilder();

        // Goal
        if (!string.IsNullOrWhiteSpace(brief.Goal))
        {
            sb.Append("<h3>Goal</h3>");
            sb.Append(RenderInlineMarkdown(brief.Goal.Trim()));
        }

        // Inputs
        if (brief.Inputs.Count > 0)
        {
            sb.Append("<h3>Inputs</h3>");
            RenderList(sb, brief.Inputs);
        }

        // Outputs
        if (brief.Outputs.Count > 0)
        {
            sb.Append("<h3>Outputs</h3>");
            RenderList(sb, brief.Outputs);
        }

        // Acceptance
        if (brief.AcceptanceCriteria.Count > 0)
        {
            sb.Append("<h3>Acceptance</h3>");
            RenderList(sb, brief.AcceptanceCriteria);
        }

        // Notes
        if (!string.IsNullOrWhiteSpace(brief.Notes))
        {
            sb.Append("<h3>Notes</h3>");
            sb.Append(RenderInlineMarkdown(brief.Notes.Trim()));
        }

        // Out of Scope
        if (brief.OutOfScope.Count > 0)
        {
            sb.Append("<h3>Out of Scope</h3>");
            RenderList(sb, brief.OutOfScope);
        }

        return sb.ToString();
    }

    private static void RenderList(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.Append("<ul>");
        foreach (var item in items)
        {
            sb.Append("<li>");
            sb.Append(RenderInlineMarkdown(item.Trim()));
            sb.Append("</li>");
        }
        sb.Append("</ul>");
    }

    /// <summary>
    /// Render a short text fragment with inline code and HTML escaping.
    /// Wraps in a &lt;p&gt; tag if the text is a non-empty paragraph.
    /// </summary>
    public static string RenderInlineMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Split on backtick pairs for inline code
        var result = new StringBuilder("<p>");
        int i = 0;
        while (i < text.Length)
        {
            int backtick = text.IndexOf('`', i);
            if (backtick < 0)
            {
                // No more backticks - append rest escaped
                result.Append(EscapeHtml(text[i..]));
                break;
            }

            // Append text before backtick, escaped
            if (backtick > i)
                result.Append(EscapeHtml(text[i..backtick]));

            // Find closing backtick
            int close = text.IndexOf('`', backtick + 1);
            if (close < 0)
            {
                // Unmatched backtick - treat as literal
                result.Append(EscapeHtml(text[backtick..]));
                break;
            }

            // Emit code span
            string code = text[(backtick + 1)..close];
            result.Append("<code>");
            result.Append(EscapeHtml(code));
            result.Append("</code>");
            i = close + 1;
        }
        result.Append("</p>");
        return result.ToString();
    }

    public static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
