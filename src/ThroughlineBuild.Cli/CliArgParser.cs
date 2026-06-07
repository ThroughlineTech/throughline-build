namespace ThroughlineBuild.Cli;

/// <summary>
/// Small helpers for extracting well-known key/value CLI flags from an args list
/// before the positional argument dispatcher runs. Extracted tokens are removed
/// from the returned remaining list so verb dispatch is unaffected.
/// </summary>
public static class CliArgParser
{
    private const string BatchImplementFlag = "--batch-implement";

    /// <summary>
    /// Scans <paramref name="args"/> for agent-override flags and returns the
    /// extracted values together with the remaining args (with the matched tokens
    /// removed).
    /// </summary>
    /// <param name="args">The full argument list, typically after the bool pre-pass.</param>
    /// <returns>
    /// A tuple of:
    ///   agentAll        - value of --agent (applies to all phases)
    ///   agentPlan       - value of --agent-plan
    ///   agentImpl       - value of --agent-implement
    ///   agentReview     - value of --agent-review
    ///   remaining       - args with the extracted tokens removed
    /// </returns>
    public static (string? agentAll, string? agentPlan, string? agentImpl, string? agentReview, IReadOnlyList<string> remaining)
        ExtractAgentFlags(IReadOnlyList<string> args)
    {
        string? agentAll = null;
        string? agentPlan = null;
        string? agentImpl = null;
        string? agentReview = null;

        var remaining = new List<string>(args.Count);
        int i = 0;
        while (i < args.Count)
        {
            var a = args[i];
            if (a == "--agent" && i + 1 < args.Count)
            {
                agentAll = args[i + 1];
                i += 2;
            }
            else if (a == "--agent-plan" && i + 1 < args.Count)
            {
                agentPlan = args[i + 1];
                i += 2;
            }
            else if (a == "--agent-implement" && i + 1 < args.Count)
            {
                agentImpl = args[i + 1];
                i += 2;
            }
            else if (a == "--agent-review" && i + 1 < args.Count)
            {
                agentReview = args[i + 1];
                i += 2;
            }
            else
            {
                remaining.Add(a);
                i++;
            }
        }

        return (agentAll, agentPlan, agentImpl, agentReview, remaining);
    }

    /// <summary>
    /// Scans <paramref name="args"/> for <c>--batch-implement</c> and extracts its value.
    /// When the flag is present with a comma-separated list the list is parsed and returned.
    /// When the flag is present with no following value (bare flag, or the next token starts
    /// with <c>--</c>), <c>isAllChildren</c> is set to <c>true</c> as an explicit sentinel
    /// meaning "batch all eligible direct children"; this is NOT the same as an empty list.
    /// When the flag is absent all returned values are null/false.
    /// Malformed explicit lists (empty id, bad shape, duplicate) are still an error.
    /// </summary>
    public static (IReadOnlyList<string>? ticketIds, bool isAllChildren, string? error, IReadOnlyList<string> remaining)
        ExtractBatchImplementFlag(IReadOnlyList<string> args)
    {
        List<string>? ticketIds = null;
        bool allChildren = false;
        var remaining = new List<string>(args.Count);

        int i = 0;
        while (i < args.Count)
        {
            if (args[i] != BatchImplementFlag)
            {
                remaining.Add(args[i]);
                i++;
                continue;
            }

            if (ticketIds is not null || allChildren)
                return (null, false, "Error: --batch-implement may be specified only once", remaining);

            // Bare flag: next token is missing or starts with '--'. Treat as "all children".
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                allChildren = true;
                i += 1; // consume only the flag, not the following token
                continue;
            }

            var parseResult = ParseBatchImplementList(args[i + 1]);
            if (parseResult.Error is not null)
                return (null, false, parseResult.Error, remaining);

            ticketIds = parseResult.TicketIds.ToList();
            i += 2;
        }

        return (ticketIds, allChildren, null, remaining);
    }

    private static (IReadOnlyList<string> TicketIds, string? Error) ParseBatchImplementList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (Array.Empty<string>(), "Error: --batch-implement requires a non-empty comma-separated ticket list");

        var parts = raw.Split(',');
        var ticketIds = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            var ticketId = part.Trim();
            if (ticketId.Length == 0)
                return (Array.Empty<string>(), "Error: --batch-implement list contains an empty ticket id");

            if (!IsTicketIdShape(ticketId))
                return (Array.Empty<string>(), $"Error: malformed ticket id in --batch-implement list: {ticketId}");

            if (!seen.Add(ticketId))
                return (Array.Empty<string>(), $"Error: duplicate ticket id in --batch-implement list: {ticketId}");

            ticketIds.Add(ticketId);
        }

        return (ticketIds, null);
    }

    private static bool IsTicketIdShape(string value)
    {
        var dash = value.IndexOf('-');
        if (dash <= 0 || dash == value.Length - 1)
            return false;

        for (int i = 0; i < dash; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]))
                return false;
        }

        for (int i = dash + 1; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the value of a key/value flag (e.g. "--flag value") from the args list,
    /// or null if the flag is absent or has no following value.
    /// Does not modify the list.
    /// </summary>
    public static string? GetFlagValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Returns the first token in <paramref name="args"/> that starts with "--" but is
    /// neither a recognized bool flag nor a recognized value flag, or null when every flag
    /// is recognized. A value flag also consumes the following token as its value, so a
    /// value that happens to look like a word (or even a flag) is never misreported as
    /// unknown. Scanning begins at index 1 because <paramref name="args"/> starts with the
    /// verb token. Tokens that do not start with "--" (positional values) are ignored.
    /// </summary>
    public static string? FindUnknownFlag(
        IReadOnlyList<string> args,
        IReadOnlySet<string> boolFlags,
        IReadOnlySet<string> valueFlags)
    {
        int i = 1; // skip the verb token
        while (i < args.Count)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            if (valueFlags.Contains(a))
            {
                i += 2; // consume the flag and its value (value is never treated as a flag)
                continue;
            }
            if (boolFlags.Contains(a))
            {
                i++;
                continue;
            }
            return a; // unrecognized
        }
        return null;
    }

    /// <summary>
    /// Extracts ticket IDs from the argument list for multi-ticket dispatch.
    /// Scans from args[1] forward (args[0] is the verb). Any token that does NOT
    /// start with '--' is considered a ticket ID. Scanning stops at the first '--'-prefixed token.
    /// </summary>
    /// <param name="args">The full argument list, with args[0] being the verb.</param>
    /// <returns>
    /// A tuple of:
    ///   ticketIds   - list of extracted ticket IDs in order
    ///   remaining   - all tokens from the first flag onward
    /// If args has fewer than 2 elements, both lists are empty.
    /// </returns>
    public static (IReadOnlyList<string> TicketIds, IReadOnlyList<string> Remaining) ExtractTicketIds(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return (Array.Empty<string>(), Array.Empty<string>());

        var ticketIds = new List<string>();
        var remaining = new List<string>();

        int i = 1;
        // Scan for ticket IDs (non-flag tokens)
        while (i < args.Count && !args[i].StartsWith("--"))
        {
            ticketIds.Add(args[i]);
            i++;
        }

        // Collect remaining tokens (flags and their values)
        while (i < args.Count)
        {
            remaining.Add(args[i]);
            i++;
        }

        return (ticketIds, remaining);
    }
}
