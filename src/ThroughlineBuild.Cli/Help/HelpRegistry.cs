namespace ThroughlineBuild.Cli;

/// <summary>
/// Collects <see cref="CommandHelp"/> contributions and provides lookup by name and enumeration by group.
/// Thread-safety is not guaranteed; populate during startup before any concurrent reads.
/// </summary>
public sealed class HelpRegistry
{
    private readonly Dictionary<string, CommandHelp> _byName =
        new Dictionary<string, CommandHelp>(StringComparer.Ordinal);

    /// <summary>
    /// Registers a command's help contribution.
    /// If a contribution for the same name was already registered it is replaced.
    /// </summary>
    public void Register(CommandHelp help) => _byName[help.Name] = help;

    /// <summary>
    /// Returns the help record for <paramref name="name"/>, or <c>null</c> if no contribution
    /// has been registered for that command name.
    /// </summary>
    public CommandHelp? TryGet(string name) =>
        _byName.TryGetValue(name, out var h) ? h : null;

    /// <summary>
    /// Enumerates all registered contributions whose <see cref="CommandHelp.Group"/> matches
    /// <paramref name="group"/>, in the order they were registered.
    /// </summary>
    public IEnumerable<CommandHelp> EnumerateByGroup(CommandGroup group)
    {
        foreach (var entry in _byName.Values)
        {
            if (entry.Group == group)
                yield return entry;
        }
    }

    /// <summary>
    /// Enumerates all registered contributions in registration order.
    /// </summary>
    public IEnumerable<CommandHelp> All => _byName.Values;

    /// <summary>
    /// Returns a new, empty registry. Provided as a named factory for readability at call sites.
    /// </summary>
    public static HelpRegistry CreateEmpty() => new HelpRegistry();
}
