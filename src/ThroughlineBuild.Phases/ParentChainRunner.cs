using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Runs parent-ticket traversal, batch dispatch, and integration-branch landing.
/// </summary>
public sealed class ParentChainRunner
{
    private readonly ITicketing _ticketing;
    private readonly BuildOptions _baseOptions;
    private readonly IWorkerAgent? _batchWorker;
    private readonly BatchImplementRunner _batchImplementRunner;
    private readonly BatchReviewRunner _batchReviewRunner;
    private readonly IGitClient _git;
    private readonly ChainIntegrationBranch _integrationBranch;
    private readonly Func<string> _sessionIdGenerator;
    private readonly Func<string, ChainEventEmitter> _eventEmitterFactory;
    private readonly Func<
        ChainPhaseOptions,
        CancellationToken,
        Task<ChainResult>> _runChainAsync;
    private readonly string _workingDirectory;
    private readonly TextWriter? _output;

    public ParentChainRunner(
        ITicketing ticketing,
        BuildOptions baseOptions,
        IWorkerAgent? batchWorker,
        BatchImplementRunner batchImplementRunner,
        BatchReviewRunner batchReviewRunner,
        IGitClient git,
        ChainIntegrationBranch integrationBranch,
        Func<string> sessionIdGenerator,
        Func<string, ChainEventEmitter> eventEmitterFactory,
        Func<ChainPhaseOptions, CancellationToken, Task<ChainResult>>
            runChainAsync,
        string workingDirectory,
        TextWriter? output)
    {
        _ticketing = ticketing;
        _baseOptions = baseOptions;
        _batchWorker = batchWorker;
        _batchImplementRunner = batchImplementRunner;
        _batchReviewRunner = batchReviewRunner;
        _git = git;
        _integrationBranch = integrationBranch;
        _sessionIdGenerator = sessionIdGenerator;
        _eventEmitterFactory = eventEmitterFactory;
        _runChainAsync = runChainAsync;
        _workingDirectory = workingDirectory;
        _output = output;
    }

    private ChainEventEmitter EventEmitter(string sessionId) =>
        _eventEmitterFactory(sessionId);

    private TextWriter Output => _output ?? Console.Out;

    public async Task<ChainResult> RunAsync(
        ChainPhaseOptions options,
        Ticket parentTicket,
        IReadOnlyList<Ticket> children,
        CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // Filter to non-terminal children (skip Done and Cancelled), and never the parent
        // itself - a self-referential parent edge would otherwise recurse forever.
        // Order by ascending ticket number: TopologicalSorter preserves input order as its
        // within-level tiebreaker, so feeding it lowest-number-first makes unordered siblings
        // (same dependency level, no blocked_by edge) dispatch lowest-number-first.
        var eligible = children
            .Where(c => c.State != TicketState.Done && c.State != TicketState.Cancelled)
            .Where(c => !string.Equals(c.Uuid, parentTicket.Uuid, StringComparison.Ordinal))
            .OrderBy(c => TicketIdOrdering.Number(c.Id))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // Build a level-based execution schedule from sibling blocked_by relations so that
        // dependent siblings are serialized while independent siblings still run in order.
        var siblingGraph = await BuildSiblingGraphAsync(eligible, ct).ConfigureAwait(false);
        var levels = TopologicalSorter.ComputeLevels(siblingGraph);

        // Print the dependency order derived from Plane before any phase runs so a wrong
        // or missing edge is visible up front. Tickets in the same level have no blocked_by
        // edge between them and are unordered relative to each other (Brief 17).
        new ChainDryRunPlanner(_ticketing, Output).PrintDispatchOrder(options.TicketId, levels);

        var integrationBaseRef = options.ChainTargetBranch ?? _baseOptions.TargetBranch;
        var integrationNames = PhaseWorktreeLayout.Compute(parentTicket.Id, parentTicket.Title, _workingDirectory);
        var integrationBranch = ChainIntegrationBranch.BranchName(parentTicket);
        var integrationWorktreePath = integrationNames.WorktreePath;

        // Capture the target head SHA once at chain start so ChainCommitRangeHelper can
        // later compute the range of commits the chain has produced (Brief 08).
        string? chainStartSha = null;
        try
        {
            chainStartSha = await _git.RevParseAsync(integrationBaseRef, _workingDirectory, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal: chainStartSha stays null */ }

        string? sharedWorktreePath = null;
        if (eligible.Count > 0)
        {
            var createResult = await _integrationBranch.EnsureIntegrationWorktreeAsync(
                integrationBranch,
                integrationBaseRef,
                integrationWorktreePath,
                ct).ConfigureAwait(false);
            if (createResult.Success)
            {
                sharedWorktreePath = createResult.AbsolutePath ?? integrationWorktreePath;

                // TLB-546: a retained chain/{slug} branch from a prior run stays frozen at the
                // base tip it forked from. Reconcile it with the CURRENT base before any child
                // dispatches - otherwise every child implements against a stale snapshot and the
                // divergence only surfaces at the root landing, after all the work is burned.
                var refreshFailure = await _integrationBranch.RefreshIntegrationBranchAsync(
                    parentTicket.Id, integrationBranch, sharedWorktreePath,
                    integrationBaseRef, () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
                if (refreshFailure is not null)
                {
                    totalSw.Stop();
                    return new ChainResult(
                        options.TicketId,
                        Array.Empty<ChainStep>(),
                        ChainOutcome.ParentStoppedEarly,
                        totalSw.Elapsed,
                        refreshFailure,
                        ChildResults: Array.Empty<ChainResult>());
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"[{parentTicket.Id}] integration worktree unavailable " +
                    $"({createResult.FailureReason}); cannot safely accumulate nested chain branches.");
                await EventEmitter(_sessionIdGenerator()).EmitAsync(
                    EventKind.GateFailure,
                    parentTicket.Id,
                    Phase.Chain,
                    new Dictionary<string, object>
                    {
                        ["kind"] = "integration_worktree_unavailable",
                        ["detail"] = createResult.FailureReason ?? "unknown",
                        ["branch"] = integrationBranch,
                        ["path"] = integrationWorktreePath
                    }, ct).ConfigureAwait(false);
                totalSw.Stop();
                return new ChainResult(
                    options.TicketId,
                    Array.Empty<ChainStep>(),
                    ChainOutcome.ParentStoppedEarly,
                    totalSw.Elapsed,
                    $"Could not create integration branch {integrationBranch}: {createResult.FailureReason}",
                    ChildResults: Array.Empty<ChainResult>());
            }
        }

        var allChildResults = new List<ChainResult>();
        bool anyStoppedEarly = false;
        // TLB-538/TLB-545: set when a stopped child's failure was environmental - an environment
        // gate failure or an unreachable ticketing backend. Both are global to the machine/config,
        // so the remaining siblings are marked Skipped instead of silently omitted - the operator
        // sees the full blast radius, and nothing else is dispatched into the same wall.
        bool environmentFailureDetected = false;
        string? environmentSkipReason = null;

        HashSet<string>? batchedTicketIds = null;
        if (options.BatchImplementGroup is not null
            && _batchWorker is not null
            && sharedWorktreePath is not null
            && !anyStoppedEarly)
        {
            // Candidate set for the batch. Ready children batch directly; Backlog children are
            // planned first (below) so --batch-implement engages on a freshly scaffolded op - whose
            // children are all Backlog - instead of silently selecting none and running the
            // per-ticket chain. Mid-flight states (Planning/InProgress/InReview) are not batched and
            // fall through to the per-ticket level loop.
            IReadOnlyList<Ticket> batchCandidates;
            if (options.BatchImplementGroup is ChainBatchImplementGroup.AllEligibleChildren)
            {
                batchCandidates = levels
                    .SelectMany(level => level
                        .Select(id => eligible.First(c => string.Equals(c.Id, id, StringComparison.Ordinal))))
                    .Where(t => t.State == TicketState.Ready || t.State == TicketState.Backlog)
                    .ToList();
            }
            else if (options.BatchImplementGroup is ChainBatchImplementGroup.ExplicitList explicitGroup)
            {
                batchCandidates = explicitGroup.TicketIds
                    .Where(id => eligible.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                    .Select(id => eligible.First(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
                    .Where(t => t.State == TicketState.Ready || t.State == TicketState.Backlog)
                    .ToList();
            }
            else
            {
                batchCandidates = Array.Empty<Ticket>();
            }

            // Exclude any candidate that is itself an internal node (has its own non-terminal
            // children). Such a ticket is a parent, not a leaf carrying code - batching it would
            // "implement" a parent as if it had a diff. Internal nodes must fall through to the
            // per-child level loop, which recurses them as parents (chaining their own children).
            // Applies equally to AllEligibleChildren and an explicitly listed ticket that turns
            // out to be an internal node. Each skip is logged so the downgrade is never silent.
            if (batchCandidates.Count > 0)
            {
                var leafCandidates = new List<Ticket>(batchCandidates.Count);
                foreach (var candidate in batchCandidates)
                {
                    var grandchildren = await _ticketing
                        .QueryAsync(new TicketQuery(ParentId: candidate.Uuid), ct).ConfigureAwait(false);
                    var hasLiveChildren = grandchildren.Any(
                        g => g.State != TicketState.Done && g.State != TicketState.Cancelled);
                    if (hasLiveChildren)
                    {
                        Console.Error.WriteLine(
                            $"[{parentTicket.Id}] batch-implement: skipping {candidate.Id} - it is an " +
                            "internal node (has non-terminal children); chaining it as a parent instead.");
                        await EventEmitter(_sessionIdGenerator()).EmitAsync(
                            EventKind.GateFailure,
                            parentTicket.Id,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["kind"] = "batch_skip_internal_node",
                                ["ticket"] = candidate.Id
                            }, ct).ConfigureAwait(false);
                        continue;
                    }
                    leafCandidates.Add(candidate);
                }
                batchCandidates = leafCandidates;
            }

            if (batchCandidates.Count > 0)
            {
                // Plan any Backlog candidates up front so the batch implement session has a Ready
                // plan for each (the implement brief reads each ticket's description as its plan).
                // Only the implement->review->ship is batched; planning stays per-ticket. A plan
                // failure stops the chain, mirroring the per-ticket StoppedAtPlan.
                var batchTicketList = new List<Ticket>(batchCandidates.Count);
                bool planStopped = false;
                foreach (var candidate in batchCandidates)
                {
                    if (candidate.State == TicketState.Backlog)
                    {
                        var planReason = await _batchImplementRunner
                            .PlanForBatchAsync(options, candidate.Id, ct).ConfigureAwait(false);
                        if (planReason is not null)
                        {
                            allChildResults.Add(new ChainResult(candidate.Id, Array.Empty<ChainStep>(),
                                ChainOutcome.StoppedAtPlan, TimeSpan.Zero, planReason));
                            anyStoppedEarly = true;
                            planStopped = true;
                            break;
                        }
                    }
                    // Re-fetch so the batch implement brief sees the planned description and Ready state.
                    batchTicketList.Add(await _ticketing.GetAsync(candidate.Id, ct).ConfigureAwait(false));
                }

                IReadOnlyList<Ticket> batchTickets = batchTicketList;

                if (!planStopped && batchTickets.Count > 0)
                {
                    var capViolation = BatchImplementRunner.CheckBatchSizeCaps(
                        batchTickets, _baseOptions);
                    if (capViolation is not null)
                    {
                        Console.Error.WriteLine(
                            $"[{parentTicket.Id}] batch-size-fallback: cap exceeded ({capViolation}); " +
                            $"running per-ticket chain for all {batchTickets.Count} ticket(s) instead.");
                        await EventEmitter(_sessionIdGenerator()).EmitAsync(
                            EventKind.GateFailure,
                            parentTicket.Id,
                            Phase.Chain,
                            new Dictionary<string, object>
                            {
                                ["kind"] = "batch_size_cap_exceeded",
                                ["cap"] = capViolation,
                                ["ticket_count"] = batchTickets.Count
                            }, ct).ConfigureAwait(false);
                        // batchedTicketIds stays null: the now-Ready planned tickets fall through to
                        // the per-ticket level loop and resume at implement (no re-plan).
                    }
                    else
                    {
                        batchedTicketIds = new HashSet<string>(
                            batchTickets.Select(t => t.Id), StringComparer.Ordinal);

                        var batchOutcome = await _batchImplementRunner.RunBatchImplementSessionAsync(
                            options, batchTickets, sharedWorktreePath, integrationBranch, chainStartSha, ct)
                            .ConfigureAwait(false);

                        allChildResults.AddRange(batchOutcome.Results);
                        var batchFailure = batchOutcome.Results.FirstOrDefault(
                            br => !IsChainSuccess(br.Outcome) && br.Outcome != ChainOutcome.Skipped);
                        if (batchFailure is not null)
                        {
                            anyStoppedEarly = true;
                            environmentFailureDetected = batchFailure.ContainsEnvironmentalStop();
                            if (environmentFailureDetected)
                                environmentSkipReason = batchFailure.ContainsTicketingUnavailable()
                                    ? "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain"
                                    : "environment gate failure in a sibling; fix the environment once and re-run the chain";
                        }

                        if (!anyStoppedEarly
                            && _batchWorker is not null
                            && batchOutcome.ConfirmedTickets is not null
                            && batchOutcome.ConfirmedTickets.Count > 0)
                        {
                            try
                            {
                                var batchReviewPassed = await _batchReviewRunner.RunBatchReviewAndReworkAsync(
                                    batchTickets,
                                    batchOutcome.ConfirmedTickets,
                                    batchOutcome.BranchName,
                                    batchOutcome.BaseRef,
                                    sharedWorktreePath,
                                    chainStartSha,
                                    ct).ConfigureAwait(false);

                                if (!batchReviewPassed)
                                {
                                    anyStoppedEarly = true;
                                }
                                else
                                {
                                    // Ship the reviewed batch stack into the integration branch: advance
                                    // chain/<parent> to the batch tip and mark each ticket Done. The root
                                    // landing then carries it to the target, exactly like a leaf ship.
                                    var shipReason = await _integrationBranch.ShipBatchStackAsync(
                                        batchTickets, batchOutcome.BranchName, integrationBranch,
                                        sharedWorktreePath, _ticketing,
                                        () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
                                    if (shipReason is not null)
                                    {
                                        Console.Error.WriteLine($"[{parentTicket.Id}] batch ship failed: {shipReason}");
                                        anyStoppedEarly = true;
                                    }
                                }
                            }
                            catch (BatchTicketingUnavailableException ex)
                            {
                                ChainEventEmitter.RecordBatchTicketingUnavailable(
                                    allChildResults, batchTickets, ex.TicketId, ex.TicketingException);
                                anyStoppedEarly = true;
                                environmentFailureDetected = true;
                                environmentSkipReason =
                                    "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain";
                            }
                        }
                    }
                }
            }
        }
        else if (options.BatchImplementGroup is not null && !anyStoppedEarly)
        {
            // Batch implement was requested but the batch path cannot run (no batch worker wired,
            // or no eligible children to batch). Surface the downgrade loudly - a visible console
            // line plus a GateFailure-style event - instead of silently falling back to the
            // per-ticket chain. A silent downgrade reads as "batch ran" when it did not, the
            // failure mode op-31 Brief 10 calls out. Mirrors the size-cap fallback logging above.
            var downgradeReason = _batchWorker is null
                ? "no batch worker configured"
                : sharedWorktreePath is null
                    ? "no eligible children to batch"
                    : "batch path unavailable";
            Console.Error.WriteLine(
                $"[{parentTicket.Id}] batch-implement requested but {downgradeReason}; " +
                "running per-ticket chain instead.");
            await EventEmitter(_sessionIdGenerator()).EmitAsync(
                EventKind.GateFailure,
                parentTicket.Id,
                Phase.Chain,
                new Dictionary<string, object>
                {
                    ["kind"] = "batch_implement_unavailable",
                    ["reason"] = downgradeReason,
                    ["ticket_count"] = eligible.Count
                }, ct).ConfigureAwait(false);
        }

        // Accumulated provides from all previously shipped children. Each child's gate
        // receives this set so the consumes-provides preflight can check whether the child's
        // declared consumes are satisfied by upstream siblings. Updated after every successful ship.
        var accumulatedProvides = new HashSet<string>(StringComparer.Ordinal);

        foreach (var level in levels)
        {
            if (anyStoppedEarly)
                break;

            var levelTickets = level
                .Select(id => eligible.First(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
                .Where(child => batchedTicketIds is null || !batchedTicketIds.Contains(child.Id))
                .ToList();

            // Parent chains intentionally dispatch one child at a time. A successful child
            // ships into the local target before the next child resolves its base, so siblings
            // stack even when the dependency graph marks them as unordered.
            foreach (var child in levelTickets)
            {
                var startStep = new ChainStep(
                    PhaseName: "chain",
                    ReworkRoundNumber: -1,
                    Status: Status.Ok,
                    FailureReason: null,
                    Verdict: null,
                    Duration: TimeSpan.Zero,
                    PhaseSessionId: _sessionIdGenerator());
                options.OnStep?.Invoke(options.TicketId, startStep);

                // Derive the chain's prior-commit pointer before each ticket so the
                // implement brief lists the files already touched by shipped siblings.
                // Resolve the CURRENT base the same way the child's implement will
                // (BaseRefResolver advances to the local target tip as siblings ship
                // locally), so the range reflects the accumulated sibling commits rather
                // than the frozen origin (TLB-411). Best-effort: any git failure leaves
                // the pointer null, which is safe - the brief is identical to the
                // no-pointer baseline.
                ChainCommitRange? childCommitRange = null;
                if (chainStartSha is not null)
                {
                    try
                    {
                        var currentTargetSha = await _git.RevParseAsync(integrationBranch, _workingDirectory, ct)
                            .ConfigureAwait(false);
                        childCommitRange = await ChainCommitRangeHelper.ComputeAsync(
                            _git, chainStartSha, currentTargetSha, _workingDirectory, ct).ConfigureAwait(false);
                    }
                    catch { /* non-fatal: pointer stays null */ }
                }

                var childOptions = options with
                {
                    TicketId = child.Id,
                    SharedWorktreePath = null,
                    ChainCommitRange = childCommitRange,
                    Depth = options.Depth + 1,
                    VisitedTicketUuids = AddVisited(options.VisitedTicketUuids, parentTicket.Uuid),
                    ChainTargetBranch = integrationBranch,
                    ChainIntegrationWorktreePath = sharedWorktreePath ?? integrationWorktreePath,
                    AccumulatedUpstreamProvides = accumulatedProvides
                };
                var childResult = await _runChainAsync(childOptions, ct)
                    .ConfigureAwait(false);

                var ok = IsChainSuccess(childResult.Outcome);
                var doneStep = new ChainStep(
                    PhaseName: "chain",
                    ReworkRoundNumber: -1,
                    Status: ok ? Status.Ok : Status.Failed,
                    FailureReason: ok ? null : $"child {child.Id} stopped: {childResult.Outcome}",
                    Verdict: null,
                    Duration: childResult.TotalDuration,
                    PhaseSessionId: _sessionIdGenerator());
                options.OnStep?.Invoke(options.TicketId, doneStep);

                allChildResults.Add(childResult);

                if (!ok)
                {
                    anyStoppedEarly = true;
                    environmentFailureDetected = childResult.ContainsEnvironmentalStop();
                    if (environmentFailureDetected)
                        environmentSkipReason = childResult.ContainsTicketingUnavailable()
                            ? "ticketing backend unreachable while running a sibling; restore connectivity and re-run the chain"
                            : "environment gate failure in a sibling; fix the environment once and re-run the chain";
                    break;
                }

                // Accumulate the shipped child's provides so subsequent siblings can check their
                // consumes against the growing set in the consumes-provides preflight.
                if (childResult.ShippedProvides is { Count: > 0 } provides)
                    accumulatedProvides.UnionWith(provides);

                if (childResult.Outcome == ChainOutcome.ParentCompleted)
                {
                    // Accumulate the finished sub-chain into this parent's integration branch.
                    // Rebase the sub-chain branch onto the parent's branch before the fast-forward
                    // (same hazard and fix as the root landing, TLB-494): in a fresh run each
                    // sub-chain forks from the parent's current tip so a plain ff works, but a
                    // reused sub-chain branch from a prior run can have diverged. Rebasing first
                    // makes the ff valid again; a conflict stops the chain with the work left safe
                    // on the sub-chain branch.
                    var childIntegrationBranch = ChainIntegrationBranch.BranchName(child);
                    var childWorktreePath = await _integrationBranch.ResolveWorktreePathAsync(
                            childIntegrationBranch, child, ct)
                        .ConfigureAwait(false);
                    var accumulateFailure = await _integrationBranch.RebaseThenFastForwardAsync(
                        child.Id, childIntegrationBranch, childWorktreePath,
                        integrationBranch, sharedWorktreePath ?? integrationWorktreePath,
                        "chain_accumulate", () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
                    if (accumulateFailure is not null)
                    {
                        allChildResults[^1] = childResult with
                        {
                            Outcome = ChainOutcome.ParentStoppedEarly,
                            FinalRationale = accumulateFailure
                        };
                        anyStoppedEarly = true;
                        break;
                    }
                }
            }
        }

        // TLB-538/TLB-545: after an environmental stop, mark every undispatched child Skipped with
        // the reason. They were not failures and they were not silently dropped - the environment
        // must be fixed once, then a re-run picks them all up.
        if (environmentFailureDetected)
        {
            var dispatched = new HashSet<string>(allChildResults.Select(r => r.TicketId), StringComparer.Ordinal);
            foreach (var level in levels)
                foreach (var id in level)
                    if (!dispatched.Contains(id) && (batchedTicketIds is null || !batchedTicketIds.Contains(id)))
                        allChildResults.Add(new ChainResult(id, Array.Empty<ChainStep>(), ChainOutcome.Skipped,
                            TimeSpan.Zero, null,
                            SkipReason: environmentSkipReason
                                ?? "environment gate failure in a sibling; fix the environment once and re-run the chain"));
        }

        var childResults = allChildResults;

        // Root-chain landing (TLB-492): a nested parent merges its integration branch up into
        // its own parent's integration branch (the ParentCompleted merge above). The OUTERMOST
        // chain (ChainTargetBranch is null) has no parent to merge into, so it lands the
        // accumulated integration branch onto the configured target branch in the main worktree
        // - which the chain preflight pinned to that target and nothing since has moved - and
        // pushes. Without this every leaf ships locally and the whole chain's work strands on a
        // local chain/{root} branch, never reaching the target.
        string? landingRationale = null;
        if (!anyStoppedEarly
            && options.ChainTargetBranch is null
            && sharedWorktreePath is not null)
        {
            landingRationale = await _integrationBranch.LandRootIntegrationBranchAsync(
                options.TicketId, integrationBranch, sharedWorktreePath,
                _baseOptions.TargetBranch, () => EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);
            if (landingRationale is not null)
                anyStoppedEarly = true;
        }

        // Attempt parent rollup (fail-soft)
        try { await _ticketing.RollupParentAsync(options.TicketId, ct).ConfigureAwait(false); }
        catch { /* non-fatal */ }

        totalSw.Stop();
        var outcome = anyStoppedEarly ? ChainOutcome.ParentStoppedEarly : ChainOutcome.ParentCompleted;
        var finalRationale = landingRationale
            ?? (environmentFailureDetected
                ? (childResults.Any(r => r.ContainsTicketingUnavailable())
                    ? $"Ticketing backend unreachable: {string.Join(", ", childResults.Where(r => r.ContainsTicketingUnavailable()).Select(r => r.TicketId))} stopped because the ticketing service could not be reached after transport retries; remaining children were skipped. Restore connectivity, then re-run."
                    : $"Environment gate failure: {string.Join(", ", childResults.Where(r => r.ContainsEnvironmentFailure()).Select(r => r.TicketId))} stopped because the gate also fails on the untouched base ref; remaining children were skipped. Fix the environment once, then re-run.")
                : anyStoppedEarly
                    ? $"One or more children did not complete: {string.Join(", ", childResults.Where(r => !IsChainSuccess(r.Outcome) && r.Outcome != ChainOutcome.Skipped).Select(r => r.TicketId))}"
                    : $"All {eligible.Count} eligible children completed.");

        if (options.ChainTargetBranch is null && IsChainSuccess(outcome))
            await _integrationBranch.SweepChainWorktreesAsync(
                options.TicketId, EventEmitter(_sessionIdGenerator()), ct).ConfigureAwait(false);

        return new ChainResult(
            TicketId: options.TicketId,
            Steps: Array.Empty<ChainStep>(),
            Outcome: outcome,
            TotalDuration: totalSw.Elapsed,
            FinalRationale: finalRationale,
            ChildResults: childResults.AsReadOnly());
    }

    private static IReadOnlySet<string> AddVisited(IReadOnlySet<string>? visited, string uuid)
    {
        var next = visited is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(visited, StringComparer.Ordinal);
        next.Add(uuid);
        return next;
    }

    private async Task<TicketGraph> BuildSiblingGraphAsync(IReadOnlyList<Ticket> eligible, CancellationToken ct)
    {
        var graph = new TicketGraph();
        var eligibleIdSet = new HashSet<string>(eligible.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in eligible)
            graph.AddNode(ticket.Id);

        var relationsByTicket = await Task.WhenAll(eligible.Select(async ticket => new
        {
            TicketId = ticket.Id,
            Relations = await _ticketing.GetRelationsAsync(ticket.Id, ct).ConfigureAwait(false)
        })).ConfigureAwait(false);

        foreach (var ticketRelations in relationsByTicket)
        {
            foreach (var rel in ticketRelations.Relations)
            {
                if (rel.Kind == "blocked_by" && eligibleIdSet.Contains(rel.TargetId))
                    graph.AddEdge(rel.TargetId, ticketRelations.TicketId); // TargetId is the blocker
            }
        }

        return graph;
    }

    private static bool IsChainSuccess(ChainOutcome outcome) =>
        outcome is ChainOutcome.Completed
            or ChainOutcome.RatifiedObsolete
            or ChainOutcome.ParentCompleted
            or ChainOutcome.BatchImplemented;

}
