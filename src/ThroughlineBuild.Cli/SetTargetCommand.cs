using System.Diagnostics;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build settarget' verb: manage the [work].target_branch key in .build/config.toml.
/// Three modes:
///   set     - validate branch exists locally, then write target_branch under [work]
///   unset   - remove target_branch from [work]; noop if absent
///   display - print resolved target_branch value and its source ([work] or [ship] default)
///
/// Config writes use line-edit (not TOML parse+reserialize) to preserve comments and formatting.
/// </summary>
public static class SetTargetCommand
{
    /// <summary>
    /// Execute the settarget command.
    /// </summary>
    /// <param name="cwd">Working directory used to locate .build/config.toml.</param>
    /// <param name="branch">Branch name for set mode; null selects display or unset mode.</param>
    /// <param name="unset">When true, remove the target_branch key.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <param name="branchValidator">
    /// (cwd, branchName) -> bool delegate. Default runs 'git rev-parse --verify refs/heads/BRANCH'.
    /// Inject a test delegate to avoid requiring a real git repo in unit tests.
    /// </param>
    /// <returns>0 on success, 2 on config-not-found or branch-not-found error.</returns>
    public static int Execute(
        string cwd,
        string? branch,
        bool unset,
        IConsole console,
        Func<string, string, bool>? branchValidator = null)
    {
        branchValidator ??= DefaultBranchValidator;

        var configPath = BuildConfigLoader.FindConfigFile(cwd);
        if (configPath is null)
        {
            console.ErrorWriteLine("Error: .build/config.toml not found. Run 'build init' to create it.");
            return 2;
        }

        if (branch is null && !unset)
            return DisplayMode(configPath, console);

        if (unset)
            return UnsetMode(configPath, console);

        return SetMode(configPath, branch!, cwd, console, branchValidator);
    }

    // ------------------------------------------------------------------
    // Display mode: print resolved target_branch with source label
    // ------------------------------------------------------------------

    private static int DisplayMode(string configPath, IConsole console)
    {
        string content;
        try { content = File.ReadAllText(configPath); }
        catch (IOException ex)
        {
            console.ErrorWriteLine($"Error: failed to read config: {ex.Message}");
            return 2;
        }

        TomlTable root;
        try { root = Toml.ToModel(content); }
        catch (Exception ex)
        {
            console.ErrorWriteLine($"Error: failed to parse config: {ex.Message}");
            return 2;
        }

        if (root.TryGetValue("work", out var workRaw) && workRaw is TomlTable work &&
            work.TryGetValue("target_branch", out var tbRaw) && tbRaw is string targetBranch)
        {
            console.WriteLine($"target_branch = {targetBranch} (from [work])");
            return 0;
        }

        var baseBranch = "main";
        if (root.TryGetValue("ship", out var shipRaw) && shipRaw is TomlTable ship &&
            ship.TryGetValue("base_branch", out var bbRaw) && bbRaw is string bb)
        {
            baseBranch = bb;
        }

        console.WriteLine($"target_branch = {baseBranch} (default, no [work] override)");
        return 0;
    }

    // ------------------------------------------------------------------
    // Unset mode: remove target_branch from [work] via line-edit
    // ------------------------------------------------------------------

    private static int UnsetMode(string configPath, IConsole console)
    {
        string text;
        try { text = File.ReadAllText(configPath); }
        catch (IOException ex)
        {
            console.ErrorWriteLine($"Error: failed to read config: {ex.Message}");
            return 2;
        }

        var (lines, sep) = SplitLines(text);
        var (sectionStart, sectionEnd) = FindSectionRange(lines, "[work]");

        if (sectionStart < 0)
        {
            console.WriteLine("noop: [work] section not present in .build/config.toml");
            return 0;
        }

        var keyLine = FindKeyLine(lines, sectionStart + 1, sectionEnd, "target_branch");
        if (keyLine < 0)
        {
            console.WriteLine("noop: target_branch key not found in [work] section");
            return 0;
        }

        var newLines = lines.ToList();
        newLines.RemoveAt(keyLine);
        File.WriteAllText(configPath, string.Join(sep, newLines), Encoding.UTF8);
        console.WriteLine("Removed target_branch from [work] in .build/config.toml");
        return 0;
    }

    // ------------------------------------------------------------------
    // Set mode: validate branch, then write target_branch under [work] via line-edit
    // ------------------------------------------------------------------

    private static int SetMode(
        string configPath,
        string branch,
        string cwd,
        IConsole console,
        Func<string, string, bool> branchValidator)
    {
        if (!branchValidator(cwd, branch))
        {
            console.ErrorWriteLine($"Error: branch '{branch}' does not exist locally.");
            console.ErrorWriteLine($"Create it first: git checkout -b {branch}");
            return 2;
        }

        string text;
        try { text = File.ReadAllText(configPath); }
        catch (IOException ex)
        {
            console.ErrorWriteLine($"Error: failed to read config: {ex.Message}");
            return 2;
        }

        var (lines, sep) = SplitLines(text);
        var (sectionStart, sectionEnd) = FindSectionRange(lines, "[work]");

        string newText;
        if (sectionStart < 0)
        {
            // [work] section absent: append it at end of file.
            // Ensure one trailing newline then add blank-line separator + section + key.
            var append = text;
            if (!append.EndsWith("\n") && !append.EndsWith("\r\n"))
                append += "\n";
            append += $"\n[work]\ntarget_branch = \"{branch}\"\n";
            newText = append;
        }
        else
        {
            var newLines = lines.ToList();
            var keyLine = FindKeyLine(newLines, sectionStart + 1, sectionEnd, "target_branch");
            if (keyLine >= 0)
            {
                // Replace existing key line
                newLines[keyLine] = $"target_branch = \"{branch}\"";
            }
            else
            {
                // Insert new key right after [work] header
                newLines.Insert(sectionStart + 1, $"target_branch = \"{branch}\"");
            }
            newText = string.Join(sep, newLines);
        }

        File.WriteAllText(configPath, newText, Encoding.UTF8);
        console.WriteLine($"Set target_branch = \"{branch}\" under [work] in .build/config.toml");
        return 0;
    }

    // ------------------------------------------------------------------
    // Git branch validation
    // ------------------------------------------------------------------

    private static bool DefaultBranchValidator(string cwd, string branch)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--verify");
            psi.ArgumentList.Add($"refs/heads/{branch}");
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Line-edit helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Split text into lines and detect the line separator (\r\n or \n).
    /// The trailing empty string produced when text ends with a newline is preserved,
    /// so round-tripping through Split + Join produces the original text.
    /// </summary>
    private static (string[] Lines, string Sep) SplitLines(string text)
    {
        var sep = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        return (lines, sep);
    }

    /// <summary>
    /// Locate a TOML section and return [start, end) indices into <paramref name="lines"/>.
    /// start  = index of the header line (e.g. "[work]").
    /// end    = index of the next single-bracket header, or lines.Count if none follows.
    ///          Double-bracket headers ([[review.checks]]) are NOT treated as section boundaries.
    /// Returns (-1, -1) if the header is not found.
    /// </summary>
    private static (int Start, int End) FindSectionRange(IList<string> lines, string header)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == header)
            {
                start = i;
                break;
            }
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

    /// <summary>
    /// Find the index of a TOML key assignment line within the range [rangeStart, rangeEnd).
    /// Matches lines where the key is followed immediately by ' ', '=', or tab.
    /// Skips comment lines and lines that don't match.
    /// Returns -1 if not found.
    /// </summary>
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
}
