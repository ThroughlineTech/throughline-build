using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Writes an op-doc-derived <see cref="ProjectProfile"/> into the three toolchain-bearing sections of
/// .build/config.toml - <c>[project]</c>, <c>[[review.checks]]</c>, and <c>[[ship.regression_checks]]</c> -
/// while preserving every other section and its comments verbatim. This is what stops a Node repo from
/// inheriting the .NET template's <c>dotnet build</c>/<c>dotnet test</c> checks.
///
/// Pure string transform (see <see cref="Apply"/>) so it is unit-testable without disk I/O; the
/// line-edit approach is the same one <see cref="SetTargetCommand"/> uses to keep comments intact.
/// Tomlyn is used only to read the existing checks for the clobber-safety gate.
/// </summary>
public static class ConfigProfileWriter
{
    public sealed record WriteOutcome(bool Changed, string? NewText, string? SkipReason, string? Summary);

    private static readonly string[] ProjectKeysOwnedByProfile =
    {
        "language", "framework", "package_manager",
        "build_command", "test_command", "install_command", "dev_command",
    };

    /// <summary>
    /// Apply the profile to the config text. Returns the rewritten text, or a skip reason when the
    /// existing checks look hand-customized (any review check whose executable is not "dotnet") and
    /// <paramref name="force"/> is false. The pristine .NET template - or no checks at all - is
    /// always safe to overwrite.
    /// </summary>
    public static WriteOutcome Apply(string configText, ProjectProfile profile, bool force)
    {
        var skip = AlreadyConfiguredSkipReason(configText, force);
        if (skip is not null)
            return new WriteOutcome(false, null, skip, null);

        var (lines, sep) = SplitLines(configText);
        var list = lines.ToList();

        bool projectChanged = ApplyProjectKeys(list, profile);
        bool reviewChanged = ApplyArrayTables(
            list, "[[review.checks]]", "review", profile.ReviewChecks);
        bool shipChanged = ApplyArrayTables(
            list, "[[ship.regression_checks]]", "ship", profile.RegressionChecks);

        bool changed = projectChanged || reviewChanged || shipChanged;
        if (!changed)
            return new WriteOutcome(false, null, "config already matched the derived profile", null);

        var summary =
            $"[project] {(profile.Framework.Length > 0 ? profile.Framework : profile.Language)}; " +
            $"{profile.ReviewChecks.Count} review check(s), {profile.RegressionChecks.Count} regression check(s)";
        return new WriteOutcome(true, string.Join(sep, list), null, summary);
    }

    // ------------------------------------------------------------------
    // Clobber-safety gate
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a human-readable reason when op-doc profile derivation should be skipped because the
    /// config's review checks already look configured for this project (any review check whose
    /// executable is not the dotnet template default) and <paramref name="force"/> is false - i.e.
    /// exactly when <see cref="Apply"/> would refuse to overwrite them. Callers can check this BEFORE
    /// running the token-costing, rate-limit-prone worker, instead of deriving a profile only to
    /// discard it. Returns null when derivation should proceed: no checks, the pristine dotnet
    /// template, or force. See TLB-491.
    /// </summary>
    public static string? AlreadyConfiguredSkipReason(string configText, bool force)
    {
        if (force) return null;
        var (customized, why) = LooksCustomized(configText);
        return customized ? why : null;
    }

    private static (bool Customized, string? Why) LooksCustomized(string configText)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize(
                configText,
                BuildTomlSerializerContext.Default.TomlTable)
                ?? throw new InvalidOperationException("TOML document produced no root table");
        }
        catch (Exception ex)
        {
            return (true, $"could not parse existing config to check for customization ({ex.Message}); leaving it untouched");
        }

        var execs = ReadCheckExecutables(root, "review", "checks");
        // No checks yet, or every check is the pristine dotnet template default -> safe to overwrite.
        var nonDefault = execs.Where(e => !string.Equals(e, "dotnet", StringComparison.OrdinalIgnoreCase)).ToList();
        if (nonDefault.Count > 0)
        {
            return (true, $"existing [[review.checks]] look customized (executable(s): {string.Join(", ", nonDefault.Distinct())}); re-run with --force-profile to overwrite");
        }
        return (false, null);
    }

    private static IReadOnlyList<string> ReadCheckExecutables(TomlTable root, string section, string arrayKey)
    {
        var result = new List<string>();
        if (root.TryGetValue(section, out var secObj) && secObj is TomlTable sec
            && sec.TryGetValue(arrayKey, out var arrObj) && arrObj is TomlTableArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry.TryGetValue("executable", out var exeObj) && exeObj is string exe)
                    result.Add(exe);
            }
        }
        return result;
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

    private static bool ApplyArrayTables(
        List<string> lines, string header, string parentSection, IReadOnlyList<ProfileCheck> checks)
    {
        var rendered = RenderChecks(header, checks);

        int firstHeader = IndexOfTrimmed(lines, header, 0);
        if (firstHeader < 0)
        {
            // No existing array-tables of this kind: insert under the parent section.
            return InsertUnderSection(lines, parentSection, rendered);
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
            return false;

        lines.RemoveRange(firstHeader, removeEnd - firstHeader);
        lines.InsertRange(firstHeader, rendered);
        return true;
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
