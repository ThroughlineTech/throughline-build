using System.Text;

namespace ThroughlineBuild.Briefs;

/// <summary>
/// Bounds for the pre-loaded-context section. All counts are stack-agnostic; the numbers are a
/// mechanical choice, the requirement is only that the bundle is never unbounded and never silently
/// truncated (see plan section 5).
/// </summary>
public sealed record PreloadOptions(
    int MaxFiles = 12,
    int MaxCharsPerFile = 16 * 1024,
    int MaxTotalChars = 64 * 1024)
{
    public static PreloadOptions Default { get; } = new();
}

/// <summary>
/// Builds the implement brief's "Pre-loaded context" section: the contents of the files the brief
/// already points at, inlined BEFORE the worker runs so the discovery turn that would have re-read
/// them never happens (experiment 2, the inverse of the gate-output convention).
///
/// Two stack-agnostic sources feed one section:
/// - the project-convention bundle (<see cref="ProjectContext.ConventionFiles"/>) - derived once, the
///   deriver chose the paths per stack;
/// - the brief's own named inputs - the file paths parsed out of the rendered Inputs read-map in the
///   ticket DescriptionHtml.
///
/// The engine never names a file and never branches on language: it reads whatever paths the data
/// (op-doc + derived profile) declares. File contents are supplied by an injected reader so this unit
/// is pure and testable without disk; the reader returns null for any path it cannot serve (missing,
/// unreadable, or outside the worktree), which becomes a countable "(not found)" marker, never a throw.
/// </summary>
public static class PreloadedContextBuilder
{
    /// <summary>
    /// Build the section, or "" when there is nothing to pre-load (no convention files and the brief
    /// names no input paths). The returned string carries its own leading newline so an empty section
    /// substitutes inert into the template (no stray blank line).
    /// </summary>
    /// <param name="descriptionHtml">The ticket DescriptionHtml (carries the rendered Inputs read-map).</param>
    /// <param name="project">Supplies <see cref="ProjectContext.ConventionFiles"/>.</param>
    /// <param name="readFile">relative-path -> content, or null if it cannot be read. Caller confines it to the worktree.</param>
    /// <param name="options">Bounds; defaults to <see cref="PreloadOptions.Default"/>.</param>
    public static string Build(
        string? descriptionHtml,
        ProjectContext project,
        Func<string, string?> readFile,
        PreloadOptions? options = null)
    {
        var opts = options ?? PreloadOptions.Default;

        // 1. Candidate paths: convention bundle FIRST (stable across briefs -> better prompt-cache
        //    reuse), then the brief's named inputs. De-duplicated, first-seen order preserved, every
        //    path validity-checked (relative, no rooting/`..`-escape) before it can reach the reader.
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw)
        {
            if (TryNormalizeRelPath(raw, out var norm) && seen.Add(norm))
                paths.Add(norm);
        }
        foreach (var c in project.ConventionFiles)
            Add(c);
        foreach (var named in ExtractNamedInputPaths(descriptionHtml))
            Add(named);

        if (paths.Count == 0)
            return string.Empty;

        // 2. Read + render, bounded.
        var body = new StringBuilder();
        int totalChars = 0;
        int rendered = 0;
        bool capped = false;
        var overflow = new List<string>();

        foreach (var path in paths)
        {
            if (rendered >= opts.MaxFiles)
            {
                capped = true;
                overflow.Add(path);
                continue;
            }

            var content = SafeRead(readFile, path);
            if (content is null)
            {
                body.Append($"- `{path}` (not found)\n");
                rendered++;
                continue;
            }

            var (bounded, note) = BoundContent(content, opts.MaxCharsPerFile);

            // Total-budget guard: once at least one file is inlined, omit any file that would push the
            // running total past the cap - but LIST it so the omission is visible, never silent. The
            // first content file is always inlined (its own per-file cap bounds it).
            if (totalChars > 0 && totalChars + bounded.Length > opts.MaxTotalChars)
            {
                capped = true;
                overflow.Add(path);
                continue;
            }

            totalChars += bounded.Length;

            body.Append($"`{path}`{note}\n```\n");
            body.Append(bounded);
            if (!bounded.EndsWith('\n'))
                body.Append('\n');
            body.Append("```\n\n");
            rendered++;
        }

        if (capped && overflow.Count > 0)
            body.Append($"- {overflow.Count} more file(s) omitted (pre-load cap): {string.Join(", ", overflow.Select(p => $"`{p}`"))}\n");

        var sb = new StringBuilder();
        sb.Append("\n## Pre-loaded context\n\n");
        sb.Append("The files below are already in your context, current as of this brief. Do not re-open ");
        sb.Append("them with Read unless you intend to edit them.\n\n");
        sb.Append(body.ToString().TrimEnd());
        sb.Append('\n');
        return sb.ToString();
    }

    // --- Named-input extraction ----------------------------------------------------------------

    /// <summary>
    /// Pull the file paths out of the rendered Inputs read-map. BriefHtmlRenderer emits Inputs as
    /// <c>&lt;h3&gt;Inputs&lt;/h3&gt;&lt;ul&gt;&lt;li&gt;&lt;p&gt;...&lt;code&gt;path-or-symbol&lt;/code&gt;...&lt;/p&gt;&lt;/li&gt;...&lt;/ul&gt;</c>,
    /// so we scan the <c>&lt;code&gt;</c> spans between the Inputs heading and the next heading and keep
    /// the ones that look like a file PATH (contain a separator + a dotted final segment), not a symbol
    /// (<c>Survey</c>, <c>getSurvey(id)</c>) and not a route (<c>/responses/:responseId</c>). This keys
    /// on path shape, not on any language.
    /// </summary>
    internal static IReadOnlyList<string> ExtractNamedInputPaths(string? descriptionHtml)
    {
        if (string.IsNullOrEmpty(descriptionHtml))
            return Array.Empty<string>();

        int h = descriptionHtml.IndexOf("<h3>Inputs</h3>", StringComparison.OrdinalIgnoreCase);
        if (h < 0)
            return Array.Empty<string>();

        int start = h + "<h3>Inputs</h3>".Length;
        int next = descriptionHtml.IndexOf("<h3>", start, StringComparison.OrdinalIgnoreCase);
        int end = next < 0 ? descriptionHtml.Length : next;
        var slice = descriptionHtml[start..end];

        var result = new List<string>();
        int i = 0;
        while (true)
        {
            int open = slice.IndexOf("<code>", i, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
                break;
            int codeStart = open + "<code>".Length;
            int close = slice.IndexOf("</code>", codeStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                break;
            var token = HtmlUnescape(slice[codeStart..close]).Trim();
            i = close + "</code>".Length;

            if (LooksLikeFilePath(token))
                result.Add(token);
        }
        return result;
    }

    /// <summary>
    /// A code token names a file when it carries a directory separator AND a dotted final segment, and
    /// passes the relative-path validity guard. The separator is what distinguishes a path from a bare
    /// symbol; the dotted final segment rejects directory-ish and route-ish tokens; both are
    /// stack-agnostic (every stack's relative source paths look like this).
    /// </summary>
    private static bool LooksLikeFilePath(string token)
    {
        var t = token.Replace('\\', '/');
        if (t.IndexOf('/') < 0)
            return false; // bare symbol or root-level file (the latter is the convention bundle's job)
        if (!TryNormalizeRelPath(t, out var norm))
            return false;
        var lastSeg = norm[(norm.LastIndexOf('/') + 1)..];
        return lastSeg.IndexOf('.') > 0; // has an extension
    }

    /// <summary>
    /// Normalize a candidate to a clean relative path or reject it. Rejects rooted paths, drive/route
    /// colons, parent-escapes, and any token carrying whitespace or glob/markup characters - so nothing
    /// that could escape the worktree or that is plainly not a path reaches the reader.
    /// </summary>
    internal static bool TryNormalizeRelPath(string? raw, out string norm)
    {
        norm = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var t = raw.Trim().Replace('\\', '/');
        if (t.Length == 0 || t[0] == '/')
            return false; // absolute / rooted
        if (t.Contains(':') || t.Contains("//"))
            return false; // drive letter, URL scheme, route param, or empty segment
        foreach (var ch in t)
        {
            if (char.IsWhiteSpace(ch) || ch is '(' or ')' or '<' or '>' or '*' or '?' or '"' or '\'' or '`')
                return false;
        }
        foreach (var seg in t.Split('/'))
        {
            if (seg == ".." || seg == ".")
                return false; // no parent-escape, no current-dir noise
        }
        norm = t;
        return true;
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static string? SafeRead(Func<string, string?> readFile, string path)
    {
        try { return readFile(path); }
        catch { return null; }
    }

    /// <summary>Head+tail truncation with a visible marker. Returns the (possibly bounded) text and a
    /// header note (empty when untruncated).</summary>
    private static (string text, string note) BoundContent(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return (content, string.Empty);
        int half = maxChars / 2;
        var head = content[..half];
        var tail = content[(content.Length - half)..];
        var omitted = content.Length - (half * 2);
        var text = head + $"\n... [truncated: {omitted} chars] ...\n" + tail;
        return (text, $" (truncated, {content.Length} chars)");
    }

    /// <summary>Reverse of BriefHtmlRenderer.EscapeHtml. Unescape &amp;amp; LAST to avoid double-decoding.</summary>
    private static string HtmlUnescape(string s)
    {
        return s
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&amp;", "&");
    }
}
