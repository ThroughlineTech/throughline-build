namespace ThroughlineBuild.Cli;

/// <summary>
/// Small helpers for extracting well-known key/value CLI flags from an args list
/// before the positional argument dispatcher runs. Extracted tokens are removed
/// from the returned remaining list so verb dispatch is unaffected.
/// </summary>
public static class CliArgParser
{
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
