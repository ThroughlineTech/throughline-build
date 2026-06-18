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
/// The result of one pre-load build: the prompt <see cref="Section"/> the worker sees, plus countable
/// telemetry so a no-op pre-load is LOUD, never a clean-looking prompt (experiment 3, the gate-output
/// convention). Pure data - not serialized here; <c>ImplementPhase</c> flattens the fields it needs into
/// event Data dictionaries.
/// </summary>
public sealed record PreloadResult(
    string Section,
    IReadOnlyList<string> LoadedWhole,
    IReadOnlyList<string> LoadedTruncated,
    int TotalBytes,
    IReadOnlyList<string> NotFoundNamed,
    IReadOnlyList<string> NotFoundConvention,
    IReadOnlyList<string> Omitted,
    bool DeclaredButAllMissing)
{
    /// <summary>Nothing to pre-load: empty section, empty telemetry, nothing declared-but-missing.</summary>
    public static PreloadResult Empty { get; } = new(
        string.Empty,
        Array.Empty<string>(), Array.Empty<string>(), 0,
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        DeclaredButAllMissing: false);

    /// <summary>Files whose contents were inlined (whole or head+tail-truncated).</summary>
    public int FilesLoaded => LoadedWhole.Count + LoadedTruncated.Count;

    /// <summary>Files inlined but head+tail truncated by the per-file bound.</summary>
    public int FilesTruncated => LoadedTruncated.Count;

    /// <summary>Every candidate path that was requested (loaded + not-found + omitted-by-bounds).</summary>
    public int FilesRequested =>
        FilesLoaded + NotFoundNamed.Count + NotFoundConvention.Count + Omitted.Count;

    /// <summary>All not-found paths, named (declared Preload) first, then convention.</summary>
    public IReadOnlyList<string> NotFoundAll =>
        NotFoundNamed.Concat(NotFoundConvention).ToList();
}

/// <summary>
/// Builds the implement brief's "Pre-loaded context" section: the contents of the files the brief
/// already points at, inlined BEFORE the worker runs so the discovery turn that would have re-read
/// them never happens (experiment 2, the inverse of the gate-output convention).
///
/// Two stack-agnostic sources feed one section:
/// - the project-convention bundle (<see cref="ProjectContext.ConventionFiles"/>) - derived once, the
///   deriver chose the paths per stack;
/// - the brief's declared Preload paths - the positive-only file PATHS the op-doc author put in the
///   brief's <c>Preload:</c> block, rendered as a <c>&lt;h3&gt;Preload&lt;/h3&gt;</c> list in the ticket
///   DescriptionHtml (experiment 3; replaces the fragile scrape of the prose Inputs read-map).
///
/// The engine never names a file and never branches on language: it reads whatever paths the data
/// (op-doc + derived profile) declares. File contents are supplied by an injected reader so this unit
/// is pure and testable without disk; the reader returns null for any path it cannot serve (missing,
/// unreadable, or outside the worktree), which becomes COUNTABLE telemetry (never a "(not found)" line
/// in the prompt, and never a throw).
/// </summary>
public static class PreloadedContextBuilder
{
    /// <summary>
    /// Build the section + telemetry. The section is "" (and <see cref="PreloadResult.Empty"/> is
    /// returned) when there is nothing to pre-load (no convention files and no declared Preload paths),
    /// or when every candidate read missed (not-found is telemetry, never prompt noise). A non-empty
    /// section carries its own leading newline so it substitutes inert into the template.
    /// </summary>
    /// <param name="descriptionHtml">The ticket DescriptionHtml (carries the rendered Preload block).</param>
    /// <param name="project">Supplies <see cref="ProjectContext.ConventionFiles"/>.</param>
    /// <param name="readFile">relative-path -> content, or null if it cannot be read. Caller confines it to the worktree.</param>
    /// <param name="options">Bounds; defaults to <see cref="PreloadOptions.Default"/>.</param>
    public static PreloadResult Build(
        string? descriptionHtml,
        ProjectContext project,
        Func<string, string?> readFile,
        PreloadOptions? options = null)
    {
        var opts = options ?? PreloadOptions.Default;

        // 1. Candidate paths: convention bundle FIRST (stable across briefs -> better prompt-cache
        //    reuse), then the brief's declared Preload paths. De-duplicated, first-seen order preserved,
        //    every path validity-checked (relative, no rooting/`..`-escape) before it can reach the
        //    reader. Each path's SOURCE is tracked so a not-found splits named (declared Preload -
        //    suspicious) vs convention (greenfield-expected on early briefs).
        var paths = new List<string>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw, bool isNamed)
        {
            if (TryNormalizeRelPath(raw, out var norm) && seen.Add(norm))
            {
                paths.Add(norm);
                if (isNamed) named.Add(norm);
            }
        }
        foreach (var c in project.ConventionFiles)
            Add(c, isNamed: false);
        var declaredPreload = ExtractPreloadPaths(descriptionHtml);
        foreach (var p in declaredPreload)
            Add(p, isNamed: true);

        var loadedWhole = new List<string>();
        var loadedTruncated = new List<string>();
        var notFoundNamed = new List<string>();
        var notFoundConvention = new List<string>();
        var omitted = new List<string>();

        if (paths.Count == 0)
            return PreloadResult.Empty;

        // 2. Read + render, bounded. A not-found path NEVER reaches the prompt (experiment 3) - it
        //    becomes countable telemetry, split named-vs-convention, instead of a dead "(not found)" line.
        var body = new StringBuilder();
        int totalChars = 0;
        int rendered = 0;

        foreach (var path in paths)
        {
            if (rendered >= opts.MaxFiles)
            {
                omitted.Add(path);
                continue;
            }

            var content = SafeRead(readFile, path);
            if (content is null)
            {
                if (named.Contains(path)) notFoundNamed.Add(path);
                else notFoundConvention.Add(path);
                continue;
            }

            var (bounded, note, truncated) = BoundContent(content, opts.MaxCharsPerFile);

            // Total-budget guard: once at least one file is inlined, omit any file that would push the
            // running total past the cap - but LIST it so the omission is visible, never silent. The
            // first content file is always inlined (its own per-file cap bounds it).
            if (totalChars > 0 && totalChars + bounded.Length > opts.MaxTotalChars)
            {
                omitted.Add(path);
                continue;
            }

            totalChars += bounded.Length;

            body.Append($"`{path}`{note}\n```\n");
            body.Append(bounded);
            if (!bounded.EndsWith('\n'))
                body.Append('\n');
            body.Append("```\n\n");
            rendered++;
            (truncated ? loadedTruncated : loadedWhole).Add(path);
        }

        if (omitted.Count > 0)
            body.Append($"- {omitted.Count} more file(s) omitted (pre-load cap): {string.Join(", ", omitted.Select(p => $"`{p}`"))}\n");

        int filesLoaded = loadedWhole.Count + loadedTruncated.Count;

        // A brief that DECLARED Preload paths but loaded zero files is the experiment-2 no-op signal.
        // Convention-only absence does NOT trip this (greenfield-expected) - it keys on declared paths.
        bool declaredButAllMissing = declaredPreload.Count > 0 && filesLoaded == 0;

        // The section is built only from inlined files; not-found is pure telemetry now. If nothing
        // loaded, the section is "" so the template stays inert and the worker sees no noise.
        string section = filesLoaded == 0 ? string.Empty : RenderSection(body);

        return new PreloadResult(
            section,
            loadedWhole, loadedTruncated, totalChars,
            notFoundNamed, notFoundConvention, omitted,
            declaredButAllMissing);
    }

    private static string RenderSection(StringBuilder body)
    {
        var sb = new StringBuilder();
        sb.Append("\n## Pre-loaded context\n\n");
        sb.Append("The files below are already in your context, current as of this brief. Do not re-open ");
        sb.Append("them with Read unless you intend to edit them.\n\n");
        sb.Append(body.ToString().TrimEnd());
        sb.Append('\n');
        return sb.ToString();
    }

    // --- Declared-Preload extraction -----------------------------------------------------------

    /// <summary>
    /// Pull the declared file paths out of the rendered Preload block. BriefHtmlRenderer emits Preload as
    /// <c>&lt;h3&gt;Preload&lt;/h3&gt;&lt;ul&gt;&lt;li&gt;&lt;code&gt;path&lt;/code&gt;&lt;/li&gt;...&lt;/ul&gt;</c>,
    /// so every <c>&lt;code&gt;</c> token between the Preload heading and the next heading is a candidate
    /// path. Because the section is POSITIVE-ONLY (the author lists only paths, never exclusions or
    /// symbols), no symbol-vs-path heuristic is needed - <see cref="TryNormalizeRelPath"/> alone rejects
    /// anything that is not a clean relative path. Keys on <c>&lt;h3&gt;Preload&lt;/h3&gt;</c>, never on
    /// any language.
    /// </summary>
    internal static IReadOnlyList<string> ExtractPreloadPaths(string? descriptionHtml)
    {
        if (string.IsNullOrEmpty(descriptionHtml))
            return Array.Empty<string>();

        int h = descriptionHtml.IndexOf("<h3>Preload</h3>", StringComparison.OrdinalIgnoreCase);
        if (h < 0)
            return Array.Empty<string>();

        int start = h + "<h3>Preload</h3>".Length;
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

            if (TryNormalizeRelPath(token, out _))
                result.Add(token);
        }
        return result;
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

    /// <summary>Head+tail truncation with a visible marker. Returns the (possibly bounded) text, a
    /// header note (empty when untruncated), and whether truncation happened.</summary>
    private static (string text, string note, bool truncated) BoundContent(string content, int maxChars)
    {
        if (content.Length <= maxChars)
            return (content, string.Empty, false);
        int half = maxChars / 2;
        var head = content[..half];
        var tail = content[(content.Length - half)..];
        var omitted = content.Length - (half * 2);
        var text = head + $"\n... [truncated: {omitted} chars] ...\n" + tail;
        return (text, $" (truncated, {content.Length} chars)", true);
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
