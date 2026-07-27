using ThroughlineBuild.Commands;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build user-guide' verb: writes the embedded operator guide to docs/throughline_build_userguide.md.
/// Not an ITicketCommand - user-guide runs without requiring a .build/config.toml to exist.
/// </summary>
public static class UserGuideCommand
{
    /// <summary>
    /// Execute the user-guide command.
    /// </summary>
    /// <param name="cwd">The working directory in which to create docs/throughline_build_userguide.md.</param>
    /// <param name="force">When true, overwrite an existing guide file.</param>
    /// <param name="printTemplate">When true, print the guide to stdout and exit; do not write a file.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <returns>0 on success, 2 on error (file already exists without --force).</returns>
    public static int Execute(string cwd, bool force, bool printTemplate, IConsole console)
    {
        var content = UserGuideLoader.Load();

        if (printTemplate)
        {
            console.Write(content);
            return 0;
        }

        var docsDir = Path.Combine(cwd, "docs");
        var target = Path.Combine(docsDir, "throughline_build_userguide.md");

        if (File.Exists(target) && !force)
        {
            console.ErrorWriteLine($"Error: {target} already exists. Use --force to overwrite.");
            return 2;
        }

        Directory.CreateDirectory(docsDir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        console.WriteLine(Path.GetFullPath(target));
        return 0;
    }
}
