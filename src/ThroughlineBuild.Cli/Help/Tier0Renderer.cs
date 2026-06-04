using System.Text;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Renders the tier-0 help menu: group headings, aligned command-name and summary
/// columns, a global-options block, and a static footer pointing at
/// <c>build help &lt;topic&gt;</c>.
/// </summary>
public static class Tier0Renderer
{
    private static readonly CommandGroup[] GroupOrder =
        [CommandGroup.Pipeline, CommandGroup.WorkItems, CommandGroup.Configure];

    private static readonly string[] GlobalOptionFlags =
        ["-h, --help", "-V, --version", "--debug", "--quiet", "--summary-json"];

    private static readonly string[] GlobalOptionDescriptions =
    [
        "Show this help",
        "Print build version and exit",
        "Stream worker output and capture session artifacts",
        "Suppress the progress digest",
        "Emit phase completion summary as JSON",
    ];

    public const string TopicFooter =
        "Run 'build help <topic>' for reference documentation. Available topics: config, digest, exit-codes, summary.";

    private static string GroupLabel(CommandGroup group) => group switch
    {
        CommandGroup.Pipeline  => "Pipeline",
        CommandGroup.WorkItems => "Work items",
        CommandGroup.Configure => "Configure",
        _                      => group.ToString(),
    };

    /// <summary>
    /// Renders the tier-0 grouped command menu from the registered help entries.
    /// Commands within each group are listed in alphabetical order by name.
    /// Groups with no registered commands are omitted.
    /// Line endings are LF (<c>\n</c>) throughout.
    /// </summary>
    public static string Render(HelpRegistry registry)
    {
        var sb = new StringBuilder();
        sb.Append("build - Throughline Build\n\n");

        foreach (var group in GroupOrder)
        {
            var cmds = new List<CommandHelp>();
            foreach (var cmd in registry.EnumerateByGroup(group))
                cmds.Add(cmd);

            if (cmds.Count == 0)
                continue;

            cmds.Sort(static (a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            int nameWidth = 0;
            foreach (var cmd in cmds)
            {
                if (cmd.Name.Length > nameWidth)
                    nameWidth = cmd.Name.Length;
            }

            sb.Append(GroupLabel(group));
            sb.Append(":\n");

            foreach (var cmd in cmds)
            {
                sb.Append("  ");
                sb.Append(cmd.Name.PadRight(nameWidth));
                sb.Append("  ");
                sb.Append(cmd.Summary);
                sb.Append('\n');
            }

            sb.Append('\n');
        }

        int flagWidth = 0;
        for (int i = 0; i < GlobalOptionFlags.Length; i++)
        {
            if (GlobalOptionFlags[i].Length > flagWidth)
                flagWidth = GlobalOptionFlags[i].Length;
        }

        sb.Append("Global options:\n");
        for (int i = 0; i < GlobalOptionFlags.Length; i++)
        {
            sb.Append("  ");
            sb.Append(GlobalOptionFlags[i].PadRight(flagWidth));
            sb.Append("  ");
            sb.Append(GlobalOptionDescriptions[i]);
            sb.Append('\n');
        }

        sb.Append('\n');
        sb.Append(TopicFooter);
        sb.Append('\n');

        return sb.ToString();
    }
}
