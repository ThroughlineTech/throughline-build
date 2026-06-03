using System.Text;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Renders the tier-1 help block for a single command: a summary header,
/// usage, options, exit codes, and examples sections.  Empty sections are
/// omitted.  Only command-specific options
/// (<see cref="OptionDescription.IsGlobal"/> = false) are shown; global
/// options are suppressed so the global block in tier-0 is not repeated.
/// Line endings are LF (<c>\n</c>) throughout.
/// </summary>
public static class Tier1Renderer
{
    /// <summary>
    /// Renders the per-command help block from a single
    /// <see cref="CommandHelp"/> contribution.  The renderer is pure: it
    /// does not reach into any registry or access sibling commands.
    /// </summary>
    public static string Render(CommandHelp help)
    {
        var sb = new StringBuilder();

        // Header: "build <name> - <summary>"
        sb.Append("build ");
        sb.Append(help.Name);
        sb.Append(" - ");
        sb.Append(help.Summary);
        sb.Append("\n\n");

        // Usage section (omit when the usage string is empty)
        if (!string.IsNullOrEmpty(help.Usage))
        {
            sb.Append("Usage:\n");
            var lines = help.Usage.Split('\n');
            foreach (var line in lines)
            {
                sb.Append("  build ");
                sb.Append(line);
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        // Options section: command-specific only (IsGlobal = false)
        var ownOptions = new List<OptionDescription>();
        foreach (var opt in help.Options)
            if (!opt.IsGlobal)
                ownOptions.Add(opt);

        if (ownOptions.Count > 0)
        {
            int flagWidth = 0;
            foreach (var opt in ownOptions)
                if (opt.Flag.Length > flagWidth)
                    flagWidth = opt.Flag.Length;

            sb.Append("Options:\n");
            foreach (var opt in ownOptions)
            {
                sb.Append("  ");
                sb.Append(opt.Flag.PadRight(flagWidth));
                sb.Append("  ");
                sb.Append(opt.Description);
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        // Exit codes section (omit when empty)
        if (help.ExitCodes.Count > 0)
        {
            int codeWidth = 0;
            foreach (var ec in help.ExitCodes)
            {
                int len = ec.Code.ToString().Length;
                if (len > codeWidth)
                    codeWidth = len;
            }

            sb.Append("Exit codes:\n");
            foreach (var ec in help.ExitCodes)
            {
                sb.Append("  ");
                sb.Append(ec.Code.ToString().PadRight(codeWidth));
                sb.Append("  ");
                sb.Append(ec.Meaning);
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        // Examples section (omit when empty)
        if (help.Examples.Count > 0)
        {
            bool hasAnyAnnotation = false;
            foreach (var ex in help.Examples)
                if (!string.IsNullOrEmpty(ex.Annotation))
                {
                    hasAnyAnnotation = true;
                    break;
                }

            sb.Append("Examples:\n");

            if (hasAnyAnnotation)
            {
                int cmdWidth = 0;
                foreach (var ex in help.Examples)
                    if (ex.Command.Length > cmdWidth)
                        cmdWidth = ex.Command.Length;

                foreach (var ex in help.Examples)
                {
                    sb.Append("  ");
                    sb.Append(ex.Command.PadRight(cmdWidth));
                    sb.Append("  ");
                    sb.Append(ex.Annotation ?? "");
                    sb.Append('\n');
                }
            }
            else
            {
                foreach (var ex in help.Examples)
                {
                    sb.Append("  ");
                    sb.Append(ex.Command);
                    sb.Append('\n');
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
