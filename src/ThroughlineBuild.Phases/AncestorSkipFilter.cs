using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Determines whether a ticket should be skipped because one of its ancestors failed,
/// and continuePastFailure is false. Returns a synthesized Skipped ChainResult if the
/// ticket should be skipped, or null if it should run normally.
/// </summary>
public static class AncestorSkipFilter
{
    private static readonly IReadOnlySet<ChainOutcome> SuccessOutcomes = new HashSet<ChainOutcome>
    {
        ChainOutcome.Completed,
        ChainOutcome.RatifiedObsolete,
        ChainOutcome.ParentCompleted
    };

    /// <summary>
    /// Returns a synthesized Skipped ChainResult when <paramref name="ticketId"/> has a
    /// failed ancestor and <paramref name="continuePastFailure"/> is false.
    /// Returns null when the ticket should run normally.
    /// </summary>
    /// <param name="ticketId">The ticket to check.</param>
    /// <param name="completedResults">Results for tickets that have already run.</param>
    /// <param name="edges">Dependency edges as (Blocker, Blocked) pairs.</param>
    /// <param name="continuePastFailure">When true, skip-filter is disabled.</param>
    public static ChainResult? ShouldSkip(
        string ticketId,
        IReadOnlyDictionary<string, ChainResult> completedResults,
        IEnumerable<(string Blocker, string Blocked)> edges,
        bool continuePastFailure)
    {
        if (continuePastFailure)
            return null;

        // Build a fast lookup: blocked -> list of blockers.
        var blockerMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (blocker, blocked) in edges)
        {
            if (!blockerMap.TryGetValue(blocked, out var list))
            {
                list = new List<string>();
                blockerMap[blocked] = list;
            }
            list.Add(blocker);
        }

        // Walk ancestors (BFS) of ticketId.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(ticketId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;

            // Do not check ticketId itself, only its ancestors.
            if (!StringComparer.Ordinal.Equals(current, ticketId))
            {
                if (completedResults.TryGetValue(current, out var result))
                {
                    if (!SuccessOutcomes.Contains(result.Outcome))
                    {
                        // Found a failed ancestor - synthesize a Skipped result.
                        return new ChainResult(
                            TicketId: ticketId,
                            Steps: Array.Empty<ChainStep>(),
                            Outcome: ChainOutcome.Skipped,
                            TotalDuration: TimeSpan.Zero,
                            FinalRationale: null,
                            SkipReason: $"skipped (ancestor {current} failed)");
                    }
                }
            }

            // Enqueue blockers of current.
            if (blockerMap.TryGetValue(current, out var blockers))
            {
                foreach (var blocker in blockers)
                    queue.Enqueue(blocker);
            }
        }

        return null;
    }
}
