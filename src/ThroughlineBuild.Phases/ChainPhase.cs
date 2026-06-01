using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

public record ChainPhaseOptions(
    string TicketId,
    bool Debug,
    Action<ChainStep>? OnStep = null,
    bool NoAutoResolve = false,
    string? SharedWorktreePath = null);

public class ChainPhase
{
    private const int MaxReworkRounds = 2;

    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly Func<BuildOptions, PlanPhase> _planFactory;
    private readonly Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> _implementFactory;
    private readonly Func<BuildOptions, ReviewPhase> _reviewFactory;
    private readonly Func<BuildOptions, ShipPhase> _shipFactory;
    // Ship factory used within the parent-chain path: produces a ShipPhase with SkipDecruft=true
    // so the shared worktree is not torn down after each ticket. Falls back to _shipFactory when null.
    private readonly Func<BuildOptions, ShipPhase>? _chainShipFactory;
    private readonly Func<string> _sessionIdGenerator;
    private readonly string _workingDirectory;
    private readonly BuildOptions _baseOptions;
    private readonly Func<BuildOptions, IObsoleteRatifier>? _ratifierFactory;
    private readonly IGitClient _git;

    public ChainPhase(
        ITicketing ticketing,
        IEventSink events,
        BuildOptions baseOptions,
        Func<BuildOptions, PlanPhase> planFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<string>? sessionIdGenerator = null,
        string? workingDirectory = null,
        Func<BuildOptions, IObsoleteRatifier>? ratifierFactory = null,
        Func<BuildOptions, ShipPhase>? chainShipFactory = null,
        IGitClient? gitClient = null)
    {
        _ticketing = ticketing;
        _events = events;
        _baseOptions = baseOptions;
        _planFactory = planFactory;
        _implementFactory = implementFactory;
        _reviewFactory = reviewFactory;
        _shipFactory = shipFactory;
        _chainShipFactory = chainShipFactory;
        _sessionIdGenerator = sessionIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _ratifierFactory = ratifierFactory;
        _git = gitClient ?? new ProcessGitClient();
    }

    public async Task<ChainResult> RunAsync(ChainPhaseOptions options, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var steps = new List<ChainStep>();

        var chainSessionId = _sessionIdGenerator();

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        // Parent-ticket chain path: recurse to non-terminal children
        var chainChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (chainChildren.Count > 0)
        {
            return await RunParentChainAsync(options, ticket, chainChildren, ct).ConfigureAwait(false);
        }

        var startPhase = ticket.State switch
        {
            TicketState.Backlog => StartPhase.Plan,
            TicketState.Ready => StartPhase.Implement,
            TicketState.InReview => StartPhase.Review,
            _ => StartPhase.Refused
        };

        var startingAtPhaseStr = startPhase switch
        {
            StartPhase.Plan => "plan",
            StartPhase.Implement => "implement",
            StartPhase.Review => "review",
            _ => "refused"
        };

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ChainStart,
            TicketId: options.TicketId,
            Phase: Phase.Chain,
            Data: new Dictionary<string, object>
            {
                ["starting_at_phase"] = startingAtPhaseStr,
                ["initial_state"] = ticket.State.ToString(),
                ["chain_session_id"] = chainSessionId
            }), ct).ConfigureAwait(false);

        if (startPhase == StartPhase.Refused)
        {
            totalSw.Stop();
            var refusedResult = new ChainResult(options.TicketId, steps, ChainOutcome.RefusedInitialState,
                totalSw.Elapsed, null);
            await EmitChainEndAsync(refusedResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
            return refusedResult;
        }

        if (startPhase == StartPhase.Plan)
        {
            var sessionId = _sessionIdGenerator();
            var buildOpts = _baseOptions with { SessionId = sessionId };
            var sw = Stopwatch.StartNew();
            var planResult = await _planFactory(buildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
                .ConfigureAwait(false);
            sw.Stop();

            var planStep = new ChainStep(
                PhaseName: "plan",
                ReworkRoundNumber: -1,
                Status: planResult.Success ? Status.Ok : Status.Failed,
                FailureReason: planResult.FailureReason,
                Verdict: null,
                Duration: sw.Elapsed,
                PhaseSessionId: sessionId);
            steps.Add(planStep);
            options.OnStep?.Invoke(planStep);

            if (!planResult.Success)
            {
                if (!options.NoAutoResolve &&
                    _ratifierFactory is not null &&
                    planResult.EscalationWorkerResult is not null &&
                    IsObsoleteEscalation(planResult.EscalationWorkerResult))
                {
                    var ratifyVerdict = await RunRatificationAsync(options, steps, planResult.EscalationWorkerResult, ct)
                        .ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence = ExtractSubsumedByEvidence(planResult.EscalationWorkerResult);
                        var finalRationale = FormatSubsumedRationale(evidence);
                        await _ticketing.TransitionAsync(options.TicketId, TicketState.Done, ct).ConfigureAwait(false);
                        await _ticketing.CreateCommentAsync(options.TicketId, "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>", ct).ConfigureAwait(false);
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: chainSessionId,
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.TicketSubsumed,
                            TicketId: options.TicketId,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }), ct).ConfigureAwait(false);
                        totalSw.Stop();
                        var ratified = new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence);
                        await EmitChainEndAsync(ratified, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                        return ratified;
                    }
                    // Ratifier rejected - fall through to StoppedAtPlan
                }
                totalSw.Stop();
                var stoppedAtPlan = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtPlan,
                    totalSw.Elapsed, null);
                await EmitChainEndAsync(stoppedAtPlan, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return stoppedAtPlan;
            }
        }

        if (startPhase == StartPhase.Plan || startPhase == StartPhase.Implement)
        {
            var chainResult = await RunImplementReviewLoopAsync(options, steps, chainSessionId, 0, null, totalSw, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                var finalResult = chainResult with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
        }
        else
        {
            var chainResult = await RunReviewBranchAsync(options, steps, chainSessionId, 0, totalSw, ct)
                .ConfigureAwait(false);
            if (chainResult is not null)
            {
                totalSw.Stop();
                var finalResult = chainResult with { TotalDuration = totalSw.Elapsed };
                await EmitChainEndAsync(finalResult, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
                return finalResult;
            }
        }

        var shipSessionId = _sessionIdGenerator();
        var shipBuildOpts = _baseOptions with { SessionId = shipSessionId };
        var shipSw = Stopwatch.StartNew();
        // When running inside a parent-chain shared worktree, use the chain ship factory (SkipDecruft=true).
        var activeShipFactory = (options.SharedWorktreePath is not null && _chainShipFactory is not null)
            ? _chainShipFactory
            : _shipFactory;
        var shipResult = await activeShipFactory(shipBuildOpts).RunAsync(options.TicketId, _workingDirectory, ct)
            .ConfigureAwait(false);
        shipSw.Stop();

        var shipStep = new ChainStep(
            PhaseName: "ship",
            ReworkRoundNumber: -1,
            Status: shipResult.Success ? Status.Ok : Status.Failed,
            FailureReason: shipResult.FailureReason,
            Verdict: null,
            Duration: shipSw.Elapsed,
            PhaseSessionId: shipSessionId);
        steps.Add(shipStep);
        options.OnStep?.Invoke(shipStep);

        totalSw.Stop();
        if (!shipResult.Success)
        {
            var stoppedAtShip = new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtShip,
                totalSw.Elapsed, null);
            await EmitChainEndAsync(stoppedAtShip, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
            return stoppedAtShip;
        }

        var completed = new ChainResult(options.TicketId, steps, ChainOutcome.Completed, totalSw.Elapsed, null);
        await EmitChainEndAsync(completed, chainSessionId, options.TicketId, ct).ConfigureAwait(false);
        return completed;
    }

    private async Task EmitChainEndAsync(ChainResult result, string chainSessionId, string ticketId, CancellationToken ct)
    {
        var reworkRounds = result.Steps.Count(s => s.PhaseName == "implement" && s.ReworkRoundNumber >= 1);
        var data = new Dictionary<string, object>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["phases_run"] = result.Steps.Count,
            ["rework_rounds"] = reworkRounds,
            ["total_duration_ms"] = (long)result.TotalDuration.TotalMilliseconds
        };
        var preview = RationalePreview(result.FinalRationale);
        if (preview != null)
            data["final_rationale_preview"] = preview;

        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ChainEnd,
            TicketId: ticketId,
            Phase: Phase.Chain,
            Data: data), ct).ConfigureAwait(false);
    }

    private async Task<ChainResult?> RunImplementReviewLoopAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int startRound,
        ReviewFeedback? initialFeedback,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        int round = startRound;
        ReviewFeedback? feedback = initialFeedback;

        while (true)
        {
            var implSessionId = _sessionIdGenerator();
            var implBuildOpts = _baseOptions with { SessionId = implSessionId };
            var implPhaseOpts = new ImplementPhaseOptions(feedback, options.SharedWorktreePath);
            var implSw = Stopwatch.StartNew();
            var implResult = await _implementFactory(implBuildOpts, implPhaseOpts)
                .RunAsync(options.TicketId, _workingDirectory, ct).ConfigureAwait(false);
            implSw.Stop();

            var implStep = new ChainStep(
                PhaseName: "implement",
                ReworkRoundNumber: round,
                Status: implResult.Success ? Status.Ok : Status.Failed,
                FailureReason: implResult.FailureReason,
                Verdict: null,
                Duration: implSw.Elapsed,
                PhaseSessionId: implSessionId);
            steps.Add(implStep);
            options.OnStep?.Invoke(implStep);

            if (!implResult.Success)
            {
                if (!options.NoAutoResolve &&
                    _ratifierFactory is not null &&
                    implResult.EscalationWorkerResult is not null &&
                    IsObsoleteEscalation(implResult.EscalationWorkerResult))
                {
                    var ratifyVerdict = await RunRatificationAsync(options, steps, implResult.EscalationWorkerResult, ct)
                        .ConfigureAwait(false);
                    if (ratifyVerdict.Kind == VerdictKind.Pass)
                    {
                        var evidence = ExtractSubsumedByEvidence(implResult.EscalationWorkerResult);
                        var finalRationale = FormatSubsumedRationale(evidence);
                        await _ticketing.TransitionAsync(options.TicketId, TicketState.Done, ct).ConfigureAwait(false);
                        await _ticketing.CreateCommentAsync(options.TicketId, "<p>" + WebUtility.HtmlEncode(finalRationale) + "</p>", ct).ConfigureAwait(false);
                        await _events.EmitAsync(new WorkflowEvent(
                            SessionId: chainSessionId,
                            Timestamp: DateTimeOffset.UtcNow,
                            Kind: EventKind.TicketSubsumed,
                            TicketId: options.TicketId,
                            Phase: Phase.Chain,
                            Data: new Dictionary<string, object>
                            {
                                ["ticket_id"] = options.TicketId,
                                ["subsumed_by_commit"] = evidence?.Commit ?? "",
                                ["files"] = evidence?.Files.ToArray() ?? Array.Empty<string>(),
                                ["rationale"] = evidence?.Rationale ?? ""
                            }), ct).ConfigureAwait(false);
                        totalSw.Stop();
                        return new ChainResult(options.TicketId, steps, ChainOutcome.RatifiedObsolete,
                            totalSw.Elapsed, finalRationale, evidence);
                    }
                    // Ratifier rejected - fall through to StoppedAtImplement
                }
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtImplement,
                    TimeSpan.Zero, null);
            }

            var reviewResult = await RunOneReviewAsync(options, steps, ct).ConfigureAwait(false);

            if (reviewResult.abort is not null)
                return reviewResult.abort;

            var rv = reviewResult.verdict!;

            if (rv.Kind == VerdictKind.Pass)
                return null;

            if (rv.Kind == VerdictKind.Fail)
                return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                    TimeSpan.Zero, rv.Rationale);

            if (round < MaxReworkRounds)
            {
                feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1);
                await _events.EmitAsync(new WorkflowEvent(
                    SessionId: chainSessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.ReworkRound,
                    TicketId: options.TicketId,
                    Phase: Phase.Implement,
                    Data: new Dictionary<string, object>
                    {
                        ["round"] = round + 1,
                        ["verdict_that_triggered"] = "Rework",
                        ["rationale_preview"] = RationalePreview(rv.Rationale) ?? ""
                    }), ct).ConfigureAwait(false);
                round++;
            }
            else
            {
                return new ChainResult(options.TicketId, steps, ChainOutcome.ReworkCapExceeded,
                    TimeSpan.Zero, rv.Rationale);
            }
        }
    }

    private async Task<ChainResult?> RunReviewBranchAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        string chainSessionId,
        int round,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        var reviewResult = await RunOneReviewAsync(options, steps, ct).ConfigureAwait(false);

        if (reviewResult.abort is not null)
            return reviewResult.abort;

        var rv = reviewResult.verdict!;

        if (rv.Kind == VerdictKind.Pass)
            return null;

        if (rv.Kind == VerdictKind.Fail)
            return new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, rv.Rationale);

        var feedback = new ReviewFeedback(rv.Rationale, rv.ChecksFailed, round + 1);
        await _events.EmitAsync(new WorkflowEvent(
            SessionId: chainSessionId,
            Timestamp: DateTimeOffset.UtcNow,
            Kind: EventKind.ReworkRound,
            TicketId: options.TicketId,
            Phase: Phase.Implement,
            Data: new Dictionary<string, object>
            {
                ["round"] = round + 1,
                ["verdict_that_triggered"] = "Rework",
                ["rationale_preview"] = RationalePreview(rv.Rationale) ?? ""
            }), ct).ConfigureAwait(false);
        return await RunImplementReviewLoopAsync(options, steps, chainSessionId, round + 1, feedback, totalSw, ct)
            .ConfigureAwait(false);
    }

    private async Task<(ChainResult? abort, Verdict? verdict)> RunOneReviewAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        CancellationToken ct)
    {
        var revSessionId = _sessionIdGenerator();
        var revBuildOpts = _baseOptions with { SessionId = revSessionId };
        var revSw = Stopwatch.StartNew();
        var revResult = await _reviewFactory(revBuildOpts)
            .RunAsync(options.TicketId, _workingDirectory, ct).ConfigureAwait(false);
        revSw.Stop();

        if (!revResult.Success)
        {
            var failedRevStep = new ChainStep(
                PhaseName: "review",
                ReworkRoundNumber: -1,
                Status: Status.Failed,
                FailureReason: revResult.FailureReason,
                Verdict: null,
                Duration: revSw.Elapsed,
                PhaseSessionId: revSessionId);
            steps.Add(failedRevStep);
            options.OnStep?.Invoke(failedRevStep);
            return (new ChainResult(options.TicketId, steps, ChainOutcome.StoppedAtReview,
                TimeSpan.Zero, revResult.FailureReason), null);
        }

        var revStep = new ChainStep(
            PhaseName: "review",
            ReworkRoundNumber: -1,
            Status: Status.Ok,
            FailureReason: null,
            Verdict: revResult.Verdict,
            Duration: revSw.Elapsed,
            PhaseSessionId: revSessionId);
        steps.Add(revStep);
        options.OnStep?.Invoke(revStep);

        return (null, new Verdict(revResult.Verdict!.Value, revResult.VerdictRationale ?? "", revResult.ChecksFailed));
    }

    private static string FormatSubsumedRationale(SubsumedByEvidence? evidence)
    {
        var commit = evidence?.Commit ?? "(unknown)";
        var rationale = evidence?.Rationale ?? "(no rationale)";
        var files = evidence?.Files is { Count: > 0 } f ? string.Join(", ", f) : "(none)";
        return $"Subsumed by {commit}: {rationale}; files: {files}";
    }

    private static string? RationalePreview(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return null;
        return rationale.Length <= 200 ? rationale : rationale.Substring(0, 200);
    }

    private static bool IsObsoleteEscalation(WorkerResult r)
    {
        if (r.Status != Status.Escalate) return false;
        if (!r.Metadata.TryGetValue("escalation", out var escalationObj)) return false;
        if (escalationObj is not JsonElement escalationElem ||
            escalationElem.ValueKind != JsonValueKind.Object) return false;
        if (!escalationElem.TryGetProperty("reason", out var reasonElem)) return false;
        if (reasonElem.ValueKind != JsonValueKind.String) return false;
        return string.Equals(reasonElem.GetString(), "obsolete", StringComparison.OrdinalIgnoreCase);
    }

    private static SubsumedByEvidence? ExtractSubsumedByEvidence(WorkerResult r)
    {
        if (!r.Metadata.TryGetValue("escalation", out var escalationObj)) return null;
        if (escalationObj is not JsonElement escalationElem ||
            escalationElem.ValueKind != JsonValueKind.Object) return null;
        if (!escalationElem.TryGetProperty("subsumed_by", out var subsumedByElem) ||
            subsumedByElem.ValueKind != JsonValueKind.Object) return null;

        var commit = subsumedByElem.TryGetProperty("commit", out var commitElem) &&
                     commitElem.ValueKind == JsonValueKind.String
            ? commitElem.GetString() : null;
        var rationale = subsumedByElem.TryGetProperty("rationale", out var rationaleElem) &&
                        rationaleElem.ValueKind == JsonValueKind.String
            ? rationaleElem.GetString() : null;

        if (string.IsNullOrEmpty(commit) || string.IsNullOrEmpty(rationale)) return null;

        var files = new List<string>();
        if (subsumedByElem.TryGetProperty("files", out var filesElem) &&
            filesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElem in filesElem.EnumerateArray())
            {
                if (fileElem.ValueKind == JsonValueKind.String)
                {
                    var f = fileElem.GetString();
                    if (!string.IsNullOrEmpty(f)) files.Add(f);
                }
            }
        }

        return new SubsumedByEvidence(commit, files.AsReadOnly(), rationale);
    }

    private async Task<Verdict> RunRatificationAsync(
        ChainPhaseOptions options,
        List<ChainStep> steps,
        WorkerResult escalateResult,
        CancellationToken ct)
    {
        var sessionId = _sessionIdGenerator();
        var buildOpts = _baseOptions with { SessionId = sessionId };
        var ratifier = _ratifierFactory!(buildOpts);

        var ticket = await _ticketing.GetAsync(options.TicketId, ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var verdict = await ratifier.RatifyAsync(ticket, escalateResult, ct).ConfigureAwait(false);
        sw.Stop();

        var ratifyStep = new ChainStep(
            PhaseName: "ratify",
            ReworkRoundNumber: -1,
            Status: verdict.Kind == VerdictKind.Pass ? Status.Ok : Status.Failed,
            FailureReason: verdict.Kind != VerdictKind.Pass ? verdict.Rationale : null,
            Verdict: verdict.Kind,
            Duration: sw.Elapsed,
            PhaseSessionId: sessionId);
        steps.Add(ratifyStep);
        options.OnStep?.Invoke(ratifyStep);

        return verdict;
    }

    private async Task<ChainResult> RunParentChainAsync(
        ChainPhaseOptions options,
        Ticket parentTicket,
        IReadOnlyList<Ticket> children,
        CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // Filter to non-terminal children (skip Done and Cancelled), and never the parent
        // itself - a self-referential parent edge would otherwise recurse forever.
        var eligible = children
            .Where(c => c.State != TicketState.Done && c.State != TicketState.Cancelled)
            .Where(c => !string.Equals(c.Uuid, parentTicket.Uuid, StringComparison.Ordinal))
            .ToList();

        // A parent chain operates exactly one level deep: it runs its direct children.
        // If any eligible child has live children of its own, the tree is deeper than this
        // command handles. Stop and tell the operator to chain the intermediate ticket
        // directly, rather than recursing (which previously ran away into Plane's rate limiter).
        var deeperTickets = new List<string>();
        foreach (var child in eligible)
        {
            var grandchildren = await _ticketing
                .QueryAsync(new TicketQuery(ParentId: child.Uuid), ct).ConfigureAwait(false);
            var liveGrandchildren = grandchildren
                .Where(g => g.State != TicketState.Done && g.State != TicketState.Cancelled)
                .Where(g => !string.Equals(g.Uuid, child.Uuid, StringComparison.Ordinal))
                .ToList();
            if (liveGrandchildren.Count > 0)
                deeperTickets.Add(child.Id);
        }

        if (deeperTickets.Count > 0)
        {
            var notice = new ChainStep(
                PhaseName: "chain",
                ReworkRoundNumber: -1,
                Status: Status.Failed,
                FailureReason: $"grandchildren present under {string.Join(", ", deeperTickets)}",
                Verdict: null,
                Duration: TimeSpan.Zero,
                PhaseSessionId: _sessionIdGenerator());
            options.OnStep?.Invoke(notice);

            totalSw.Stop();
            return new ChainResult(
                TicketId: options.TicketId,
                Steps: Array.Empty<ChainStep>(),
                Outcome: ChainOutcome.ParentHasGrandchildren,
                TotalDuration: totalSw.Elapsed,
                FinalRationale: $"Tree is deeper than one level. Chain the intermediate ticket(s) directly: {string.Join(", ", deeperTickets)}.");
        }

        // Build a level-based execution schedule from sibling blocked_by relations so that
        // dependent siblings are serialized while independent siblings still run in order.
        var siblingGraph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
        var levels = TopologicalSorter.ComputeLevels(siblingGraph);

        // Create one shared worktree for the entire parent chain. Each child ticket creates
        // its own branch inside this worktree; ship skips decruft so the worktree stays alive
        // until all children are done. The worktree is removed once here at chain end.
        var sharedWorktreeNames = PhaseWorktreeLayout.Compute(parentTicket.Id, parentTicket.Title, _workingDirectory);
        string? sharedWorktreePath = null;
        string? baseRefForSharedWt = null;
        try
        {
            (baseRefForSharedWt, _) = await BaseRefResolver.ResolveAsync(_git, _workingDirectory, _baseOptions.TargetBranch, ct).ConfigureAwait(false);
        }
        catch
        {
            // If we cannot resolve the base ref we fall back to null and let each child
            // handle ref resolution independently (standalone worktree per ticket path).
            baseRefForSharedWt = null;
        }

        // Capture the target head SHA once at chain start so ChainCommitRangeHelper can
        // later compute the range of commits the chain has produced (Brief 08).
        string? chainStartSha = null;
        try
        {
            if (baseRefForSharedWt is not null)
                chainStartSha = await _git.RevParseAsync(baseRefForSharedWt, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal: chainStartSha stays null */ }

        if (baseRefForSharedWt is not null && eligible.Count > 0)
        {
            var createResult = await _git.CreateWorktreeAsync(
                sharedWorktreeNames.WorktreePath,
                // Initial branch name in the shared worktree - first child will immediately
                // create its own branch and switch, so this is just a placeholder.
                $"chain/{sharedWorktreeNames.Slug}",
                baseRefForSharedWt,
                _workingDirectory,
                ct).ConfigureAwait(false);
            if (createResult.Success)
                sharedWorktreePath = sharedWorktreeNames.WorktreePath;
            // If creation fails (e.g. path already exists) we fall back to per-ticket worktrees.
        }

        var semaphore = new SemaphoreSlim(1, 1);
        var allChildResults = new List<ChainResult>();
        bool anyStoppedEarly = false;

        foreach (var level in levels)
        {
            if (anyStoppedEarly)
                break;

            var levelTickets = level
                .Select(id => eligible.First(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
                .ToList();

            var levelTasks = levelTickets.Select(async child =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var startStep = new ChainStep(
                        PhaseName: "chain",
                        ReworkRoundNumber: -1,
                        Status: Status.Ok,
                        FailureReason: null,
                        Verdict: null,
                        Duration: TimeSpan.Zero,
                        PhaseSessionId: _sessionIdGenerator());
                    options.OnStep?.Invoke(startStep);

                    var childOptions = options with { TicketId = child.Id, SharedWorktreePath = sharedWorktreePath };
                    var childResult = await RunAsync(childOptions, ct).ConfigureAwait(false);

                    var ok = IsChainSuccess(childResult.Outcome);
                    var doneStep = new ChainStep(
                        PhaseName: "chain",
                        ReworkRoundNumber: -1,
                        Status: ok ? Status.Ok : Status.Failed,
                        FailureReason: ok ? null : $"child {child.Id} stopped: {childResult.Outcome}",
                        Verdict: null,
                        Duration: childResult.TotalDuration,
                        PhaseSessionId: _sessionIdGenerator());
                    options.OnStep?.Invoke(doneStep);

                    return childResult;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var levelResults = (await Task.WhenAll(levelTasks).ConfigureAwait(false)).ToList();
            allChildResults.AddRange(levelResults);
            anyStoppedEarly = levelResults.Any(r => !IsChainSuccess(r.Outcome));
        }

        var childResults = allChildResults;

        // Remove the shared worktree now that all children are done. Failure is non-fatal.
        if (sharedWorktreePath is not null)
        {
            try
            {
                var decrufter = new WorktreeDecrufter(_git);
                await decrufter.DecruftAsync(sharedWorktreePath, _workingDirectory, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal: ticket transitions are already committed */ }
        }

        // Attempt parent rollup (fail-soft)
        try { await _ticketing.RollupParentAsync(options.TicketId, ct).ConfigureAwait(false); }
        catch { /* non-fatal */ }

        totalSw.Stop();
        var outcome = anyStoppedEarly ? ChainOutcome.ParentStoppedEarly : ChainOutcome.ParentCompleted;
        var finalRationale = anyStoppedEarly
            ? $"One or more children did not complete: {string.Join(", ", childResults.Where(r => !IsChainSuccess(r.Outcome)).Select(r => r.TicketId))}"
            : $"All {eligible.Count} eligible children completed.";

        return new ChainResult(
            TicketId: options.TicketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: totalSw.Elapsed,
            FinalRationale: finalRationale,
            ChildResults: childResults.AsReadOnly());
    }

    private async Task<TicketGraph> BuildSiblingGraphAsync(IReadOnlyList<Ticket> eligible, CancellationToken ct)
    {
        var graph = new TicketGraph();
        var eligibleIdSet = new HashSet<string>(eligible.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in eligible)
            graph.AddNode(ticket.Id);

        foreach (var ticket in eligible)
        {
            var relations = await _ticketing.GetRelationsAsync(ticket.Id, ct).ConfigureAwait(false);
            foreach (var rel in relations)
            {
                if (rel.Kind == "blocked_by" && eligibleIdSet.Contains(rel.TargetId))
                    graph.AddEdge(rel.TargetId, ticket.Id); // TargetId is the blocker
            }
        }

        return graph;
    }

    private static bool IsChainSuccess(ChainOutcome outcome) =>
        outcome is ChainOutcome.Completed
            or ChainOutcome.RatifiedObsolete
            or ChainOutcome.ParentCompleted;

    private enum StartPhase { Plan, Implement, Review, Refused }
}
