using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

public sealed class ParallelDispatcher
{
    private readonly Func<ChainPhaseOptions, CancellationToken, Task<ChainResult>> _runChain;
    private readonly IEventSink _eventSink;
    private readonly int _maxConcurrency;

    // Dispatch session ID used as the SessionId field on DispatchStart/DispatchEnd events.
    private readonly Func<string> _sessionIdGenerator;

    public ParallelDispatcher(ChainPhase chainPhase, IEventSink eventSink, int maxConcurrency,
        Func<string>? sessionIdGenerator = null)
        : this((opts, ct) => chainPhase.RunAsync(opts, ct), eventSink, maxConcurrency, sessionIdGenerator)
    {
    }

    // Internal constructor used by tests to inject a fake chain runner.
    internal ParallelDispatcher(
        Func<ChainPhaseOptions, CancellationToken, Task<ChainResult>> runChain,
        IEventSink eventSink,
        int maxConcurrency,
        Func<string>? sessionIdGenerator = null)
    {
        _runChain = runChain;
        _eventSink = eventSink;
        // Width is pinned to 1: the topological order is load-bearing; concurrency is
        // the disposable part. Running width-1 removes the cross-worker worktree races
        // that the merge-contention machinery existed to handle. The maxConcurrency
        // parameter is retained for API stability; it is ignored.
        _maxConcurrency = 1;
        _sessionIdGenerator = sessionIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<ParallelDispatchResult> RunAsync(
        IReadOnlyList<string> ticketIds,
        TicketGraph graph,
        ChainPhaseOptions baseOptions,
        CancellationToken ct)
    {
        var dispatchSessionId = _sessionIdGenerator();
        var totalSw = Stopwatch.StartNew();

        IReadOnlyList<IReadOnlyList<string>> levels;
        try
        {
            levels = TopologicalSorter.ComputeLevels(graph);
        }
        catch (InvalidOperationException ex)
        {
            return new ParallelDispatchResult(false, Array.Empty<ChainResult>(), ex.Message);
        }

        // Print the dependency order derived from the ticket graph before any phase runs
        // so a wrong or missing edge is visible up front (Brief 17). Tickets in the same
        // level have no blocked_by edge between them and are unordered relative to each other.
        PrintDispatchOrder(ticketIds, levels);

        // Emit DispatchStart
        await _eventSink.EmitAsync(new WorkflowEvent(
            SessionId: dispatchSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.DispatchStart,
            TicketId: string.Empty,
            Phase: Phase.Chain,
            Data: new Dictionary<string, object>
            {
                ["ticket_count"] = ticketIds.Count,
                ["level_count"] = levels.Count,
                ["max_concurrency"] = _maxConcurrency
            }), ct).ConfigureAwait(false);

        var allResults = new List<ChainResult>();
        bool failed = false;
        string? failureReason = null;
        // TLB-538/TLB-545: once any root's subtree hits an environmental stop (environment gate
        // failure, or the ticketing backend unreachable), every root dispatched after it would
        // fail the same way (the environment is global). Remaining roots return Skipped without
        // running. Width is pinned to 1, so the flags are observed in dispatch order.
        bool environmentFailed = false;
        string? environmentSkipReason = null;
        ChainOutcome environmentOutcome = ChainOutcome.GateEnvironmentFailure;

        foreach (var level in levels)
        {
            if (ct.IsCancellationRequested)
            {
                failed = true;
                failureReason = "cancelled";
                break;
            }

            // Only dispatch IDs in this level that are in the requested ticketIds set
            var levelIds = level
                .Where(id => ticketIds.Contains(id))
                .ToList();

            if (levelIds.Count == 0)
                continue;

            var semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            var levelTasks = levelIds.Select(async id =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (environmentFailed)
                    {
                        return new ChainResult(id, Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                            TimeSpan.Zero, null,
                            SkipReason: environmentSkipReason);
                    }
                    var opts = baseOptions with { TicketId = id };
                    var result = await _runChain(opts, ct).ConfigureAwait(false);
                    if (result.ContainsEnvironmentalStop())
                    {
                        environmentFailed = true;
                        if (result.ContainsTicketingUnavailable())
                        {
                            environmentOutcome = ChainOutcome.TicketingUnavailable;
                            environmentSkipReason = $"ticketing backend unreachable in {id}; restore connectivity and re-run";
                        }
                        else
                        {
                            environmentOutcome = ChainOutcome.GateEnvironmentFailure;
                            environmentSkipReason = $"environment gate failure in {id}; fix the environment once and re-run";
                        }
                    }
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            ChainResult[] levelResults;
            try
            {
                levelResults = await Task.WhenAll(levelTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                failed = true;
                failureReason = "cancelled";
                break;
            }

            allResults.AddRange(levelResults);

            // Check for failures: any outcome not in the success set stops further levels
            var successOutcomes = new HashSet<ChainOutcome>
            {
                ChainOutcome.Completed,
                ChainOutcome.RatifiedObsolete,
                ChainOutcome.ParentCompleted
            };

            var failedResults = levelResults.Where(r => !successOutcomes.Contains(r.Outcome)).ToList();
            if (failedResults.Count > 0)
            {
                failed = true;
                var firstFail = failedResults[0];
                failureReason = $"ticket {firstFail.TicketId} stopped with outcome {firstFail.Outcome}";
                break;
            }
        }

        totalSw.Stop();

        // TLB-538/TLB-545: roots in levels never reached (the loop breaks after a failed level)
        // get the same Skipped marker as same-level roots, so the report shows the full blast radius.
        if (environmentFailed)
        {
            var seen = new HashSet<string>(allResults.Select(r => r.TicketId), StringComparer.OrdinalIgnoreCase);
            foreach (var id in ticketIds)
                if (!seen.Contains(id))
                    allResults.Add(new ChainResult(id, Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                        TimeSpan.Zero, null,
                        SkipReason: environmentSkipReason));
        }

        ChainOutcome? preservedOutcome = failed && allResults.Count > 0
            && allResults.All(r => r.Outcome == ChainOutcome.RefusedDirtyTree)
            ? ChainOutcome.RefusedDirtyTree
            : null;
        // TLB-538/TLB-545: surface the environmental stop in the dispatch outcome and exit code -
        // the operator's next action (fix the environment / restore connectivity, re-run
        // everything) differs from a normal partial failure (triage the one stopped ticket).
        if (preservedOutcome is null && environmentFailed)
            preservedOutcome = environmentOutcome;
        var dispatchOutcome = preservedOutcome is { } po
            ? po.ToString()
            : failed ? "partial" : "ok";

        // Emit DispatchEnd
        await _eventSink.EmitAsync(new WorkflowEvent(
            SessionId: dispatchSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.DispatchEnd,
            TicketId: string.Empty,
            Phase: Phase.Chain,
            Data: new Dictionary<string, object>
            {
                ["outcome"] = dispatchOutcome,
                ["total_duration_ms"] = (long)totalSw.Elapsed.TotalMilliseconds
            }), ct).ConfigureAwait(false);

        return new ParallelDispatchResult(
            Success: !failed,
            Results: allResults.AsReadOnly(),
            FailureReason: failureReason,
            PreservedOutcome: preservedOutcome);
    }

    /// <summary>
    /// Prints the dependency-ordered dispatch sequence before the first phase runs.
    /// Each level is a set of tickets with no blocked_by edge between them; within a
    /// level they are unordered relative to each other, making a missing edge obvious.
    /// </summary>
    private static void PrintDispatchOrder(
        IReadOnlyList<string> ticketIds,
        IReadOnlyList<IReadOnlyList<string>> levels)
    {
        Console.WriteLine($"dispatch order ({ticketIds.Count} ticket{(ticketIds.Count == 1 ? "" : "s")}, {levels.Count} level{(levels.Count == 1 ? "" : "s")}):");
        for (int i = 0; i < levels.Count; i++)
        {
            // Only include IDs that are in the requested ticketIds set (same filter as dispatch loop).
            var level = levels[i].Where(id => ticketIds.Contains(id)).ToList();
            if (level.Count == 0)
                continue;
            var ticketList = string.Join(", ", level);
            var unorderedNote = level.Count > 1 ? " (unordered)" : "";
            Console.WriteLine($"  level {i + 1}: {ticketList}{unorderedNote}");
        }
    }
}
