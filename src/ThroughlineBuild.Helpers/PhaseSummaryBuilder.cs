using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Helpers;

// Deterministic builder for end-of-phase completion summaries (TLB-123).
//
// Inputs are primitives extracted from phase-result records by the orchestrator
// (Program.cs) - we keep Helpers free of any reference to ThroughlineBuild.Phases
// to avoid a circular project dependency.
//
// Every field traces to one of: the recorded WorkflowEvent stream, the Plane
// Ticket record, local git commands wrapped by IGitClient, or phase-supplied
// primitives that already exist in the in-memory result types. No additional
// LLM calls are made.
public static class PhaseSummaryBuilder
{
    public static PlanPhaseSummary BuildPlan(
        string ticketId,
        bool success,
        string? riskLabel,
        string? sizeLabel,
        string? plannedAtSha,
        string? failureReason,
        Ticket? ticket,
        IReadOnlyList<WorkflowEvent> events,
        string sessionId,
        string? planeUrl,
        string? sessionArtifactsPath)
    {
        var (from, to) = FindStateTransition(events, Phase.Plan);
        var ops = CollectPlaneOps(events, Phase.Plan);
        var (wallMs, outTok, cacheRead) = CollectWorkerStats(events, Phase.Plan);

        return new PlanPhaseSummary
        {
            PhaseKind = "Plan",
            TicketId = ticketId,
            Success = success,
            FailureReason = failureReason,
            FailedAt = success ? null : "worker",
            SessionArtifactsPath = sessionArtifactsPath,
            PlaneUrl = planeUrl,
            SizeLabel = sizeLabel,
            RiskLabel = riskLabel,
            StateFrom = from,
            StateTo = to,
            PlaneOps = ops,
            InvestigatedAtSha = plannedAtSha,
            WorkerWallMs = wallMs,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
        };
    }

    public static ImplementPhaseSummary BuildImplement(
        string ticketId,
        bool success,
        string? branchName,
        string? commitSha,
        string? failureReason,
        IReadOnlyList<WorkflowEvent> events,
        IReadOnlyList<DiffEntry> diff,
        IReadOnlyList<string> commitOnelines,
        int commitCount,
        string? planeUrl,
        string? sessionArtifactsPath)
    {
        var (from, to) = FindStateTransition(events, Phase.Implement);
        var (wallMs, outTok, cacheRead) = CollectWorkerStats(events, Phase.Implement);

        int added = 0, edited = 0, testsAdded = 0;
        foreach (var e in diff)
        {
            if (e.Kind == DiffKind.Added) added++;
            else edited++; // Modified, Deleted, Renamed all count as edits in the summary
            if (e.Kind == DiffKind.Added
                && (e.Path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                    || e.Path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)))
            {
                testsAdded++;
            }
        }

        var commitShas = new List<string>();
        foreach (var line in commitOnelines)
        {
            var idx = line.IndexOf(' ');
            commitShas.Add(idx > 0 ? line.Substring(0, idx) : line);
        }

        return new ImplementPhaseSummary
        {
            PhaseKind = "Implement",
            TicketId = ticketId,
            Success = success,
            FailureReason = failureReason,
            FailedAt = success ? null : "worker",
            SessionArtifactsPath = sessionArtifactsPath,
            PlaneUrl = planeUrl,
            BranchName = branchName,
            CommitSha = commitSha,
            CommitCount = commitCount,
            CommitShas = commitShas,
            FilesChanged = diff.Count,
            FilesAdded = added,
            FilesEdited = edited,
            TestsAddedCount = testsAdded,
            StateFrom = from,
            StateTo = to,
            WorkerWallMs = wallMs,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
        };
    }

    public static ReviewPhaseSummary BuildReview(
        string ticketId,
        bool success,
        string? verdict,
        string? rationale,
        IReadOnlyList<string> checksFailed,
        string? failureReason,
        IReadOnlyList<WorkflowEvent> events,
        string? planeUrl,
        string? sessionArtifactsPath)
    {
        var (from, to) = FindStateTransition(events, Phase.Review);
        var (wallMs, outTok, cacheRead) = CollectWorkerStats(events, Phase.Review);

        return new ReviewPhaseSummary
        {
            PhaseKind = "Review",
            TicketId = ticketId,
            Success = success,
            FailureReason = failureReason,
            FailedAt = success ? null : "verifier",
            SessionArtifactsPath = sessionArtifactsPath,
            PlaneUrl = planeUrl,
            Verdict = verdict,
            VerdictRationale = rationale,
            ChecksFailed = checksFailed,
            StateFrom = from,
            StateTo = to,
            WorkerWallMs = wallMs,
            OutputTokens = outTok,
            CacheReadTokens = cacheRead,
        };
    }

    public static ShipPhaseSummary BuildShip(
        string ticketId,
        bool success,
        string? branchName,
        string? mergedSha,
        string? failureReason,
        string? failedAtStage,
        IReadOnlyList<WorkflowEvent> events,
        IReadOnlyList<DiffEntry> diff,
        string? planeUrl,
        string? sessionArtifactsPath)
    {
        var (from, to) = FindStateTransition(events, Phase.Ship);
        bool archived = false;
        foreach (var ev in events)
        {
            if (ev.Phase != Phase.Ship || ev.Kind != EventKind.TicketWrite) continue;
            if (ev.Data.TryGetValue("action", out var actObj) && actObj is string act)
            {
                if (act == "decruft" && ev.Data.TryGetValue("halted_at", out var haltObj)
                    && haltObj is string halt && string.Equals(halt, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    archived = true;
                }
            }
        }

        return new ShipPhaseSummary
        {
            PhaseKind = "Ship",
            TicketId = ticketId,
            Success = success,
            FailureReason = failureReason,
            FailedAt = failedAtStage,
            SessionArtifactsPath = sessionArtifactsPath,
            PlaneUrl = planeUrl,
            BranchName = branchName,
            MergedSha = mergedSha,
            FilesChanged = diff.Count,
            StateFrom = from,
            StateTo = to,
            Archived = archived,
        };
    }

    // ---- shared helpers ----

    private static (string? from, string? to) FindStateTransition(
        IReadOnlyList<WorkflowEvent> events, Phase phase)
    {
        // Most recent StateTransition for this phase wins.
        string? from = null, to = null;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var ev = events[i];
            if (ev.Phase != phase || ev.Kind != EventKind.StateTransition) continue;
            from = TryGetString(ev.Data, "from");
            to = TryGetString(ev.Data, "to");
            if (from is not null || to is not null) break;
        }
        return (from, to);
    }

    private static IReadOnlyList<string> CollectPlaneOps(
        IReadOnlyList<WorkflowEvent> events, Phase phase)
    {
        var ops = new List<string>();
        foreach (var ev in events)
        {
            if (ev.Phase != phase || ev.Kind != EventKind.TicketWrite) continue;
            var action = TryGetString(ev.Data, "action");
            if (action is not null) ops.Add(action);
        }
        return ops;
    }

    private static (long? wallMs, long? outTok, long? cacheRead) CollectWorkerStats(
        IReadOnlyList<WorkflowEvent> events, Phase phase)
    {
        // Most recent LlmCall for the phase. Tolerant of int / long / JsonElement payloads
        // (the latter shows up when events round-trip through System.Text.Json).
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var ev = events[i];
            if (ev.Phase != phase || ev.Kind != EventKind.LlmCall) continue;
            long? wall = TryGetLong(ev.Data, "wall_ms") ?? TryGetLong(ev.Data, "duration_ms");
            long? outTok = TryGetLong(ev.Data, "output_tokens") ?? TryGetLong(ev.Data, "out");
            long? cache = TryGetLong(ev.Data, "cache_read_tokens") ?? TryGetLong(ev.Data, "cache_read");
            return (wall, outTok, cache);
        }
        return (null, null, null);
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var val) || val is null) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
        return val.ToString();
    }

    private static long? TryGetLong(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var val) || val is null) return null;
        switch (val)
        {
            case long l: return l;
            case int i: return i;
            case double d: return (long)d;
            case string s when long.TryParse(s, out var ls): return ls;
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                return je.TryGetInt64(out var jel) ? jel : (long)je.GetDouble();
            case JsonElement je2 when je2.ValueKind == JsonValueKind.String && long.TryParse(je2.GetString(), out var jl):
                return jl;
            default: return null;
        }
    }
}
