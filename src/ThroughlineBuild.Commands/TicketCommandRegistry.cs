using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Commands;

public sealed class TicketCommandRegistry
{
    private readonly Dictionary<string, ITicketCommand> _commands = new(StringComparer.Ordinal);

    public void Register(string verb, ITicketCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(verb))
            throw new ArgumentException("verb must be non-empty", nameof(verb));
        if (cmd is null)
            throw new ArgumentNullException(nameof(cmd));
        _commands[verb] = cmd;
    }

    public bool TryGet(string verb, out ITicketCommand? cmd)
    {
        if (string.IsNullOrEmpty(verb))
        {
            cmd = null;
            return false;
        }
        return _commands.TryGetValue(verb, out cmd);
    }
}
