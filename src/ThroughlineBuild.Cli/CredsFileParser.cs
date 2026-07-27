namespace ThroughlineBuild.Cli;

/// <summary>
/// Values parsed from a credentials input file.
/// Any field that was absent in the file is null.
/// Key names match the config [ticketing] section so operators can reuse
/// a trimmed config file as a creds file.
/// </summary>
public record CredsFileValues(
    string? PlaneBaseUrl,
    string? PlaneWorkspaceSlug,
    string? PlaneApiToken,
    string? PlaneProjectId,
    string? PlaneProjectName);

/// <summary>
/// Parser for operator credentials input files.
/// Reads "key = value" or "key = \"value\"" lines, tolerating
/// comment lines (# ...), blank lines, and quoted or unquoted values.
/// Unknown keys are silently ignored.
/// </summary>
public static class CredsFileParser
{
    /// <summary>
    /// Parse the content of a credentials file into a <see cref="CredsFileValues"/> record.
    /// </summary>
    public static CredsFileValues Parse(string content)
    {
        string? baseUrl = null;
        string? workspaceSlug = null;
        string? apiToken = null;
        string? projectId = null;
        string? projectName = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();

            // Skip blank lines and comment lines.
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = line[..eqIndex].Trim();
            var value = ParseValue(line[(eqIndex + 1)..].Trim());
            if (value is null)
                continue;

            switch (key)
            {
                case "plane_base_url":
                    baseUrl = value;
                    break;
                case "plane_workspace_slug":
                    workspaceSlug = value;
                    break;
                case "plane_api_token":
                    apiToken = value;
                    break;
                case "plane_project_id":
                    projectId = value;
                    break;
                case "plane_project_name":
                    projectName = value;
                    break;
                    // Unknown keys are silently ignored.
            }
        }

        return new CredsFileValues(baseUrl, workspaceSlug, apiToken, projectId, projectName);
    }

    /// <summary>
    /// Strip surrounding double-quotes from a value, or return it as-is when unquoted.
    /// Inline comments after an unquoted value are stripped.
    /// Returns null for an empty value.
    /// </summary>
    private static string? ParseValue(string raw)
    {
        if (raw.Length == 0)
            return null;

        if (raw[0] == '"')
        {
            // Quoted: find the closing double-quote, take everything between them.
            var closeIndex = raw.IndexOf('"', 1);
            return closeIndex > 0 ? raw[1..closeIndex] : raw[1..];
        }

        // Unquoted: strip an inline comment that starts with ' #' or '#'.
        var hashIndex = raw.IndexOf('#');
        var value = hashIndex >= 0 ? raw[..hashIndex].TrimEnd() : raw;
        return value.Length > 0 ? value : null;
    }
}
