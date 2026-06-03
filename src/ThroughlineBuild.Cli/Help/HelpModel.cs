namespace ThroughlineBuild.Cli;

/// <summary>
/// Top-level grouping for commands shown in structured help output.
/// </summary>
public enum CommandGroup
{
    /// <summary>Commands that drive the plan/implement/review/ship/chain/rework/decompose pipeline.</summary>
    Pipeline,
    /// <summary>Commands that create or mutate work items (tickets).</summary>
    WorkItems,
    /// <summary>Commands that manage project configuration or emit reference material.</summary>
    Configure
}

/// <summary>
/// Describes a single flag or option accepted by a command.
/// </summary>
/// <param name="Flag">The flag token as it appears on the command line, e.g. "--debug".</param>
/// <param name="Description">One-line description of what the flag does.</param>
/// <param name="IsGlobal">
/// True when the flag is a global option accepted by all (or nearly all) commands;
/// false when the flag is specific to this command.
/// </param>
public record OptionDescription(string Flag, string Description, bool IsGlobal);

/// <summary>
/// Documents the meaning of one exit code produced by a command.
/// </summary>
/// <param name="Code">Numeric exit code.</param>
/// <param name="Meaning">Plain-text description of the exit condition.</param>
public record ExitCodeEntry(int Code, string Meaning);

/// <summary>
/// A concrete invocation example with an optional annotation.
/// </summary>
/// <param name="Command">Full command string as the user would type it (without the binary name).</param>
/// <param name="Annotation">Optional brief note about what the example demonstrates.</param>
public record UsageExample(string Command, string? Annotation);

/// <summary>
/// Complete machine-readable description of one CLI command's help.
/// No formatting is baked into this record; renderers consume it and apply their own layout.
/// </summary>
/// <param name="Name">The verb as matched by the dispatcher, e.g. "plan".</param>
/// <param name="Group">Which top-level group this command belongs to.</param>
/// <param name="Summary">One-line description shown in command listings.</param>
/// <param name="Usage">
/// Usage synopsis line(s), e.g. "plan &lt;ticket-id&gt; [...] [--agent &lt;name&gt;]".
/// May contain newlines when a verb has multiple forms.
/// </param>
/// <param name="Options">
/// Ordered list of options accepted by this command.
/// Global options (shared across commands) are included with <see cref="OptionDescription.IsGlobal"/> = true
/// so a renderer can decide whether to suppress or deduplicate them.
/// </param>
/// <param name="ExitCodes">Exit codes and their meanings, in ascending numeric order.</param>
/// <param name="Examples">Ordered list of usage examples.</param>
public record CommandHelp(
    string Name,
    CommandGroup Group,
    string Summary,
    string Usage,
    IReadOnlyList<OptionDescription> Options,
    IReadOnlyList<ExitCodeEntry> ExitCodes,
    IReadOnlyList<UsageExample> Examples);
