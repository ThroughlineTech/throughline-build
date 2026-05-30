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
    /// <returns>0 on success, 1 on error.</returns>
    public static int Execute(string cwd, bool force, bool printTemplate, IConsole console)
    {
        var template = ConfigTemplateLoader.Load();

        if (printTemplate)
        {
            console.Write(template);
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
        File.WriteAllText(target, template, System.Text.Encoding.UTF8);

        console.WriteLine($"Created {target}");
        console.WriteLine("Fill in the REQUIRED fields before running other build commands.");
        return 0;
    }
}
