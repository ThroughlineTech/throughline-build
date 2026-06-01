using ThroughlineBuild.Commands;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build init' verb: writes .build/config.toml from the embedded template.
/// Not an ITicketCommand - init bootstraps the config before any config is present.
/// </summary>
public static class InitCommand
{
    /// <summary>
    /// Execute the init command.
    /// </summary>
    /// <param name="cwd">The working directory in which to create .build/config.toml.</param>
    /// <param name="force">When true, overwrite an existing config file.</param>
    /// <param name="printTemplate">When true, print the template to stdout and exit; do not write a file.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <param name="planeUrl">Optional: replaces REQUIRED_PLANE_BASE_URL in the template.</param>
    /// <param name="workspace">Optional: replaces REQUIRED_PLANE_WORKSPACE_SLUG in the template.</param>
    /// <param name="projectId">Optional: replaces REQUIRED_PLANE_PROJECT_ID in the template.</param>
    /// <param name="token">Optional: replaces REQUIRED_PLANE_API_TOKEN in the template (literal token value).</param>
    /// <param name="tokenEnv">Optional: if set, the plane_api_token line is replaced with plane_api_token_env = "VALUE".</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int Execute(
        string cwd,
        bool force,
        bool printTemplate,
        IConsole console,
        string? planeUrl = null,
        string? workspace = null,
        string? projectId = null,
        string? token = null,
        string? tokenEnv = null)
    {
        var template = ConfigTemplateLoader.Load();
        var content = ApplyFlags(template, planeUrl, workspace, projectId, token, tokenEnv);

        if (printTemplate)
        {
            console.Write(content);
            return 0;
        }

        var target = Path.Combine(cwd, ".build", "config.toml");

        if (File.Exists(target) && !force)
        {
            console.ErrorWriteLine($"Error: {target} already exists. Use --force to overwrite.");
            return 1;
        }

        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        console.WriteLine($"Created {target}");
        console.WriteLine("Fill in the REQUIRED fields before running other build commands.");
        console.WriteLine("Run 'build user-guide' to write the operator setup guide to docs/.");
        return 0;
    }

    /// <summary>
    /// Applies flag values to the raw template string, replacing placeholders.
    /// </summary>
    public static string ApplyFlags(
        string template,
        string? planeUrl,
        string? workspace,
        string? projectId,
        string? token,
        string? tokenEnv)
    {
        var content = template;

        if (!string.IsNullOrEmpty(planeUrl))
            content = content.Replace("REQUIRED_PLANE_BASE_URL", planeUrl);

        if (!string.IsNullOrEmpty(workspace))
            content = content.Replace("REQUIRED_PLANE_WORKSPACE_SLUG", workspace);

        if (!string.IsNullOrEmpty(projectId))
            content = content.Replace("REQUIRED_PLANE_PROJECT_ID", projectId);

        // --token-env: replace the plane_api_token = "..." line with plane_api_token_env = "VALUE"
        // --token: replace just the placeholder value inside the existing line
        // The two flags are mutually exclusive: --token-env takes precedence if both are given.
        if (!string.IsNullOrEmpty(tokenEnv))
        {
            // Replace the literal token line with an env-var line.
            // Match the line that starts with plane_api_token = (not plane_api_token_env).
            content = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"plane_api_token\s*=\s*""[^""]*""([^\n]*)",
                $"plane_api_token_env = \"{tokenEnv}\"");
        }
        else if (!string.IsNullOrEmpty(token))
        {
            content = content.Replace("REQUIRED_PLANE_API_TOKEN", token);
        }

        return content;
    }
}
