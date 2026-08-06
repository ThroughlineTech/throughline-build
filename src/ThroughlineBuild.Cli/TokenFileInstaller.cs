using System.Text;

namespace ThroughlineBuild.Cli;

public sealed record TokenFileWriteResult(bool Success, string Message);

// 'build setup --write-token-file' persists the token this process already resolved (inline
// config, an env var, or an existing file) into a file, then points [ticketing].
// plane_api_token_file at it. It never prompts for or otherwise handles a NEW token, and never
// writes the token value anywhere but the target file - the config mutation only ever carries a
// PATH, so it is safe to log or diff. The point is to give a future non-interactive process (an
// agent harness, CI, an editor with no login-shell environment) a way to find the same token
// without depending on that process's own shell having sourced an interactive rc file. See TLB-638.
public static class TokenFileInstaller
{
    public static TokenFileWriteResult Write(
        string repoRoot, string configPath, string relativeOrAbsolutePath, string token)
    {
        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(relativeOrAbsolutePath)
                ? relativeOrAbsolutePath
                : Path.GetFullPath(relativeOrAbsolutePath, repoRoot);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, token.Trim() + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TokenFileWriteResult(false, $"could not write token file '{relativeOrAbsolutePath}': {ex.Message}");
        }

        string configText;
        try
        {
            configText = File.ReadAllText(configPath);
        }
        catch (IOException ex)
        {
            return new TokenFileWriteResult(
                false, $"wrote '{fullPath}' but could not read '{configPath}' to record it: {ex.Message}");
        }

        var configValue = relativeOrAbsolutePath.Replace('\\', '/');
        try
        {
            File.WriteAllText(
                configPath,
                SetTokenFileKey(configText, configValue),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException ex)
        {
            return new TokenFileWriteResult(false, $"wrote '{fullPath}' but could not update '{configPath}': {ex.Message}");
        }

        return new TokenFileWriteResult(
            true, $"wrote Plane API token to '{fullPath}' and set plane_api_token_file in '{configPath}'");
    }

    // Inserts or replaces the plane_api_token_file line inside [ticketing]. Line-based rather than
    // a TOML round-trip so every other byte in the file - comments, spacing, [[review.checks]] -
    // survives untouched, matching how ResolveGeneratedPlatform patches conductor.toml.
    internal static string SetTokenFileKey(string configText, string relativePath)
    {
        var eol = configText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = configText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var newLine = $"plane_api_token_file = \"{EscapeToml(relativePath)}\"";

        var inTicketing = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
                inTicketing = string.Equals(trimmed, "[ticketing]", StringComparison.Ordinal);
            if (inTicketing && trimmed.StartsWith("plane_api_token_file", StringComparison.Ordinal))
            {
                lines[i] = newLine;
                return string.Join(eol, lines);
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), "[ticketing]", StringComparison.Ordinal))
            {
                lines.Insert(i + 1, newLine);
                return string.Join(eol, lines);
            }
        }

        // No [ticketing] section - not expected for an already-loaded config, but fail safe rather
        // than silently drop the setting.
        if (lines.Count > 0 && lines[^1].Length != 0)
            lines.Add(string.Empty);
        lines.Add("[ticketing]");
        lines.Add(newLine);
        return string.Join(eol, lines);
    }

    private static string EscapeToml(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
