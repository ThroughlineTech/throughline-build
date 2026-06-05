using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements 'build op-doc spec': prints or materializes the embedded op-doc authoring spec.
/// Runs without requiring .build/config.toml.
/// </summary>
public static class OpDocSpecCommand
{
    /// <summary>
    /// Execute the op-doc spec command.
    /// </summary>
    /// <param name="cwd">The working directory in which to create docs/op-docs/op-doc-spec.md.</param>
    /// <param name="write">When true, write the spec to docs/op-docs/op-doc-spec.md; otherwise print it.</param>
    /// <param name="force">When true, overwrite an existing spec file.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <returns>0 on success, 2 on bad arguments or clobber refusal.</returns>
    public static int Execute(string cwd, bool write, bool force, IConsole console)
    {
        var content = OpDocDocsLoader.LoadSpec();

        if (!write)
        {
            console.Write(content);
            return 0;
        }

        var docsDir = Path.Combine(cwd, "docs", "op-docs");
        var target = Path.Combine(docsDir, "op-doc-spec.md");

        if (File.Exists(target) && !force)
        {
            console.ErrorWriteLine($"Error: {target} already exists. Use --force to overwrite.");
            return 2;
        }

        Directory.CreateDirectory(docsDir);
        File.WriteAllText(target, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        console.WriteLine(Path.GetFullPath(target));
        return 0;
    }
}
