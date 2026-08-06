using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Writes a validated <see cref="ProjectProfile"/> into the three toolchain-bearing sections of
/// .build/config.toml - <c>[project]</c>, <c>[[review.checks]]</c>, and <c>[[ship.regression_checks]]</c> -
/// while preserving every other section and its comments verbatim. This is what keeps a repository's
/// checks derived from its own profile rather than inherited from whatever the template shipped.
///
/// Pure string transform (see <see cref="Apply"/>) so it is unit-testable without disk I/O; the
/// line-edit approach is the same one <see cref="SetTargetCommand"/> uses to keep comments intact.
/// </summary>
public static class ConfigProfileWriter
{
    /// <summary>
    /// The result of applying a profile. Exactly one of three states holds:
    /// <list type="bullet">
    /// <item><c>Changed</c> with <c>NewText</c> - the config was rewritten.</item>
    /// <item><c>AlreadyMatched</c> - the config already said what the profile says; applying it again
    /// is a no-op success (this is what makes a re-run of the installer idempotent).</item>
    /// <item><c>SkipReason</c> - the clobber gate refused to overwrite checks it did not write.</item>
    /// </list>
    /// </summary>
    public sealed record WriteOutcome(
        bool Changed, string? NewText, string? SkipReason, string? Summary, bool AlreadyMatched = false);

    /// <summary>How an array-table run in the config compared to the run the profile renders.</summary>
    private enum ArrayTableEdit
    {
        /// <summary>The rendered run is already present byte-for-byte.</summary>
        Unchanged,

        /// <summary>No run of this kind existed; the rendered one was added.</summary>
        Inserted,

        /// <summary>A run existed with different content and was replaced. This is the clobber case.</summary>
        Replaced,
    }

    private static readonly string[] ProjectKeysOwnedByProfile =
    {
        "language", "framework", "package_manager",
        "build_command", "test_command", "install_command", "dev_command",
    };

    /// <summary>
    /// Apply the profile to the config text.
    ///
    /// The transform is computed FIRST, and the clobber gate is consulted only from its result. That
    /// ordering is what makes the write idempotent on every stack: a config that already says what the
    /// profile says is reported as <see cref="WriteOutcome.AlreadyMatched"/> without the gate ever
    /// running. When the transform would replace an EXISTING run of check entries with different
    /// content, those checks were not written by this profile, so they are treated as hand-written and
    /// preserved unless <paramref name="force"/> is set (the TLB-491 protection). Adding checks where
    /// the config had none is never a clobber.
    ///
    /// The decision is made purely by comparing rendered content against existing content, so it
    /// carries no knowledge of any toolchain, executable, or stack. See TLB-639.
    /// </summary>
    public static WriteOutcome Apply(string configText, ProjectProfile profile, bool force)
    {
        var (lines, sep) = SplitLines(configText);
        var list = lines.ToList();

        bool projectChanged = ApplyProjectKeys(list, profile);
        var reviewEdit = ApplyArrayTables(
            list, "[[review.checks]]", "review", profile.ReviewChecks);
        var shipEdit = ApplyArrayTables(
            list, "[[ship.regression_checks]]", "ship", profile.RegressionChecks);

        bool changed = projectChanged
            || reviewEdit != ArrayTableEdit.Unchanged
            || shipEdit != ArrayTableEdit.Unchanged;
        if (!changed)
        {
            return new WriteOutcome(
                false, null, null, AlreadyMatchedSummary, AlreadyMatched: true);
        }

        var skip = force ? null : ClobberSkipReason(reviewEdit, shipEdit);
        if (skip is not null)
            return new WriteOutcome(false, null, skip, null);

        var summary =
            $"[project] {(profile.Framework.Length > 0 ? profile.Framework : profile.Language)}; " +
            $"{profile.ReviewChecks.Count} review check(s), {profile.RegressionChecks.Count} regression check(s)";
        return new WriteOutcome(true, string.Join(sep, list), null, summary);
    }

    public const string AlreadyMatchedSummary = "config already matched the supplied profile";

    // ------------------------------------------------------------------
    // Clobber-safety gate
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a human-readable refusal when applying the profile would overwrite check entries that
    /// are already in the config and say something else - the proxy for "a human wrote these". Returns
    /// null when nothing existing is being overwritten (checks added where there were none, or check
    /// content already identical). The named override, <c>--force</c>, is accepted by every command
    /// that can print this message. See TLB-491 for the protection and TLB-639 for the proxy.
    /// </summary>
    private static string? ClobberSkipReason(ArrayTableEdit reviewEdit, ArrayTableEdit shipEdit)
    {
        var replaced = new List<string>();
        if (reviewEdit == ArrayTableEdit.Replaced) replaced.Add("[[review.checks]]");
        if (shipEdit == ArrayTableEdit.Replaced) replaced.Add("[[ship.regression_checks]]");
        if (replaced.Count == 0) return null;

        return $"existing {string.Join(" and ", replaced)} look customized "
            + "(they differ from the supplied profile); re-run with --force to overwrite";
    }

    // ------------------------------------------------------------------
    // [project] key replacement (in-place; preserves keys the profile does not own)
    // ------------------------------------------------------------------

    private static bool ApplyProjectKeys(List<string> lines, ProjectProfile profile)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["language"] = profile.Language,
            ["framework"] = profile.Framework,
            ["package_manager"] = profile.PackageManager,
            ["build_command"] = profile.BuildCommand,
            ["test_command"] = profile.TestCommand,
            ["install_command"] = profile.InstallCommand,
            ["dev_command"] = profile.DevCommand,
        };

        var (start, end) = FindSectionRange(lines, "[project]");
        if (start < 0)
        {
            // No [project] section: append one with the profile keys.
            EnsureTrailingBlank(lines);
            lines.Add("[project]");
            foreach (var key in ProjectKeysOwnedByProfile)
                lines.Add($"{key} = {TomlString(values[key])}");
            if (profile.ConventionFiles.Count > 0)
                lines.Add($"convention_files = {TomlStringArray(profile.ConventionFiles)}");
            return true;
        }

        bool changed = false;
        foreach (var key in ProjectKeysOwnedByProfile)
        {
            var newLine = $"{key} = {TomlString(values[key])}";
            int keyLine = FindKeyLine(lines, start + 1, end, key);
            if (keyLine >= 0)
            {
                if (lines[keyLine] != newLine) { lines[keyLine] = newLine; changed = true; }
            }
            else
            {
                lines.Insert(start + 1, newLine);
                end++;
                changed = true;
            }
        }

        // convention_files is an ARRAY key (not a scalar), so it is handled apart from the owned scalar
        // keys above. Write it only when the profile carries a bundle; replace an existing active line,
        // insert when absent. A commented template line is skipped by FindKeyLine, so it survives.
        if (profile.ConventionFiles.Count > 0)
        {
            var cfLine = $"convention_files = {TomlStringArray(profile.ConventionFiles)}";
            int cfKeyLine = FindKeyLine(lines, start + 1, end, "convention_files");
            if (cfKeyLine >= 0)
            {
                if (lines[cfKeyLine] != cfLine) { lines[cfKeyLine] = cfLine; changed = true; }
            }
            else
            {
                lines.Insert(start + 1, cfLine);
                changed = true;
            }
        }
        return changed;
    }

    // ------------------------------------------------------------------
    // [[array.table]] replacement (remove existing run, insert rendered)
    // ------------------------------------------------------------------

    private static ArrayTableEdit ApplyArrayTables(
        List<string> lines, string header, string parentSection, IReadOnlyList<ProfileCheck> checks)
    {
        var rendered = RenderChecks(header, checks);

        int firstHeader = IndexOfTrimmed(lines, header, 0);
        if (firstHeader < 0)
        {
            // No existing array-tables of this kind: insert under the parent section. Nothing a human
            // wrote is being displaced, so this is never a clobber.
            return InsertUnderSection(lines, parentSection, rendered)
                ? ArrayTableEdit.Inserted
                : ArrayTableEdit.Unchanged;
        }

        // Find the end of the consecutive run of `header` blocks, then back up over the
        // trailing blank/comment lines that belong to the *next* section.
        int runEnd = firstHeader;
        while (true)
        {
            int i = runEnd + 1;
            while (i < lines.Count && !IsHeader(lines[i])) i++;
            runEnd = i; // points at next header or EOF
            if (runEnd < lines.Count && lines[runEnd].Trim() == header)
                continue; // another block of the same kind
            break;
        }

        int removeEnd = runEnd;
        while (removeEnd > firstHeader && (IsBlank(lines[removeEnd - 1]) || IsComment(lines[removeEnd - 1])))
            removeEnd--;

        var existing = lines.GetRange(firstHeader, removeEnd - firstHeader);
        if (existing.SequenceEqual(rendered))
            return ArrayTableEdit.Unchanged;

        lines.RemoveRange(firstHeader, removeEnd - firstHeader);
        lines.InsertRange(firstHeader, rendered);
        return ArrayTableEdit.Replaced;
    }

    private static bool InsertUnderSection(List<string> lines, string section, List<string> rendered)
    {
        var (start, end) = FindSectionRange(lines, $"[{section}]");
        var block = new List<string> { "" };
        block.AddRange(rendered);

        if (start < 0)
        {
            EnsureTrailingBlank(lines);
            lines.Add($"[{section}]");
            lines.AddRange(rendered);
            return true;
        }

        // Insert after the section's content, before the next single-bracket header.
        int insertAt = end;
        while (insertAt > start + 1 && (IsBlank(lines[insertAt - 1]) || IsComment(lines[insertAt - 1])))
            insertAt--;
        lines.InsertRange(insertAt, block);
        return true;
    }

    private static List<string> RenderChecks(string header, IReadOnlyList<ProfileCheck> checks)
    {
        var rendered = new List<string>();
        for (int i = 0; i < checks.Count; i++)
        {
            if (i > 0) rendered.Add("");
            var c = checks[i];
            rendered.Add(header);
            rendered.Add($"name = {TomlString(c.Name)}");
            rendered.Add($"executable = {TomlString(c.Executable)}");
            rendered.Add($"arguments = {TomlStringArray(c.Arguments)}");
            rendered.Add($"timeout_minutes = {c.TimeoutMinutes}");
            rendered.Add($"role = {TomlString(RoleToToml(c.Role))}");
            if (c.Canary is { Count: > 0 })
                rendered.Add($"canary = {TomlCanaryArray(c.Canary)}");
            if (c.RequiredPaths is { Count: > 0 })
                rendered.Add($"required_paths = {TomlStringArray(c.RequiredPaths)}");
        }
        return rendered;
    }

    // Render the check role as the TOML string the config loader's ParseCheckRole expects. Written on
    // every check (not just advisory) so the scaffolded config states the gating decision explicitly
    // rather than leaning on the loader's implicit default - an operator can see and flip it.
    private static string RoleToToml(CheckRole role) => role switch
    {
        CheckRole.Advisory => "advisory",
        CheckRole.Setup => "setup",
        _ => "gating"
    };

    // Renders the canary files as a TOML inline-table array:
    //   canary = [{ path = "...", content = "..." }, { ... }]
    // Uses the newline-safe basic-string escaper since canary content commonly contains
    // newlines/tabs (a deliberately-broken source file).
    private static string TomlCanaryArray(IReadOnlyList<CanaryFile> canary)
    {
        var items = canary.Select(cf =>
            $"{{ path = {TomlBasicString(cf.Path)}, content = {TomlBasicString(cf.Content)} }}");
        return "[" + string.Join(", ", items) + "]";
    }

    // ------------------------------------------------------------------
    // TOML rendering + line helpers
    // ------------------------------------------------------------------

    private static string TomlString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // Newline-safe TOML basic string. TOML basic strings cannot carry literal control
    // characters, so escape (in order) backslash, double-quote, then CR/LF/TAB as the
    // two-character escape sequences. Used for canary path+content, which may contain
    // newlines and tabs. TomlString is intentionally left alone so existing outputs stay
    // byte-identical.
    private static string TomlBasicString(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
        return $"\"{escaped}\"";
    }

    private static string TomlStringArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return "[]";
        return "[" + string.Join(", ", values.Select(TomlString)) + "]";
    }

    private static void EnsureTrailingBlank(List<string> lines)
    {
        // After Split, a file ending in "\n" yields a trailing "" entry. Guarantee exactly one
        // blank line of separation before an appended section.
        if (lines.Count == 0) return;
        if (lines[^1].Length != 0) lines.Add("");
        lines.Add("");
    }

    private static (string[] Lines, string Sep) SplitLines(string text)
    {
        var sep = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        return (lines, sep);
    }

    private static (int Start, int End) FindSectionRange(IList<string> lines, string header)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == header) { start = i; break; }
        }
        if (start < 0) return (-1, -1);

        for (int i = start + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("[") && !trimmed.StartsWith("[["))
                return (start, i);
        }
        return (start, lines.Count);
    }

    private static int FindKeyLine(IList<string> lines, int rangeStart, int rangeEnd, string key)
    {
        for (int i = rangeStart; i < rangeEnd && i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length > key.Length &&
                trimmed.StartsWith(key) &&
                (trimmed[key.Length] == ' ' || trimmed[key.Length] == '=' || trimmed[key.Length] == '\t'))
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOfTrimmed(IList<string> lines, string headerTrimmed, int from)
    {
        for (int i = from; i < lines.Count; i++)
            if (lines[i].Trim() == headerTrimmed) return i;
        return -1;
    }

    private static bool IsHeader(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("[");
    }

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    private static bool IsComment(string line) => line.TrimStart().StartsWith("#");
}
