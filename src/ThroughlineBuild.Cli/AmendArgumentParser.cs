using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

internal static class AmendArgumentParser
{
    private static readonly HashSet<string> s_scalarOptions =
    [
        "size", "note", "description", "title", "priority", "type", "parent"
    ];

    private static readonly HashSet<string> s_repeatableOptions = ["label-add", "label-remove"];

    internal static bool TryParse(
        string ticketId,
        IReadOnlyList<string> args,
        int startIndex,
        out TicketCommandContext? context,
        out string? error)
    {
        var scalar = new Dictionary<string, string>(StringComparer.Ordinal);
        var repeated = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var i = startIndex; i < args.Count; i += 2)
        {
            var option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                context = null;
                error = $"unexpected argument '{option}'";
                return false;
            }

            var key = option[2..];
            if (!s_scalarOptions.Contains(key) && !s_repeatableOptions.Contains(key))
            {
                context = null;
                error = $"unknown amend option '{option}'";
                return false;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                context = null;
                error = $"{option} requires a value";
                return false;
            }

            var value = args[i + 1];
            if (s_repeatableOptions.Contains(key))
            {
                if (!repeated.TryGetValue(key, out var values))
                {
                    values = [];
                    repeated[key] = values;
                }
                values.Add(value);
            }
            else if (!scalar.TryAdd(key, value))
            {
                context = null;
                error = $"{option} may only be specified once";
                return false;
            }
        }

        var readOnlyRepeated = repeated.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(),
            StringComparer.Ordinal);
        context = new TicketCommandContext(ticketId, scalar, readOnlyRepeated);
        error = null;
        return true;
    }
}
