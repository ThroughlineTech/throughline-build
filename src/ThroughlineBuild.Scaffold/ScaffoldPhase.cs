using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Orchestrates the scaffold operation: parse an op-doc, validate it, and create
/// the corresponding plan-ticket + brief-ticket tree in the ticketing backend.
/// </summary>
public sealed class ScaffoldPhase
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly string _sessionId;

    public ScaffoldPhase(ITicketing ticketing, IEventSink events, string sessionId)
    {
        _ticketing = ticketing;
        _events = events;
        _sessionId = sessionId;
    }

    /// <summary>
    /// Run the scaffold operation.
    /// 1. Parse the op-doc file (fail-fast on parse errors).
    /// 2. Validate the parsed OpDoc (fail-fast on validation errors).
    /// 3. If !options.AcceptWarnings and warnings exist, return warning-blocked result.
    /// 4. If options.DryRun, return dry-run preview without any API calls.
    /// 5. For each Plan in dispatch order:
    ///    a. Create plan-ticket with label "plan-ticket".
    ///    b. For each Brief: create brief-ticket, then SetParentAsync to link it.
    ///    All creation steps are best-effort; failures are collected in the result.
    /// </summary>
    public async Task<ScaffoldResult> RunAsync(ScaffoldOptions options, CancellationToken ct)
    {
        // --- Step 1: parse ---
        var parseResult = OpDocParser.Parse(options.OpDocPath);

        var hardParseErrors = parseResult.Errors
            .Where(e => !e.Message.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hardParseErrors.Count > 0 || parseResult.Parsed == null)
        {
            return new ScaffoldResult(
                PlansCreated: 0,
                BriefsCreated: 0,
                CreatedTicketIds: Array.Empty<string>(),
                Failures: Array.Empty<ScaffoldFailure>(),
                WasAbortedByParseErrors: true,
                WasAbortedByValidationErrors: false,
                WasBlockedByWarnings: false,
                WasDryRun: false,
                OpTicketId: null);
        }

        var opDoc = parseResult.Parsed;

        // --- Step 2: validate ---
        var validationResult = OpDocValidator.Validate(opDoc);

        if (!validationResult.IsValid)
        {
            return new ScaffoldResult(
                PlansCreated: 0,
                BriefsCreated: 0,
                CreatedTicketIds: Array.Empty<string>(),
                Failures: Array.Empty<ScaffoldFailure>(),
                WasAbortedByParseErrors: false,
                WasAbortedByValidationErrors: true,
                WasBlockedByWarnings: false,
                WasDryRun: false,
                OpTicketId: null);
        }

        // --- Step 3: warning gate ---
        if (!options.AcceptWarnings && validationResult.Warnings.Count > 0)
        {
            return new ScaffoldResult(
                PlansCreated: 0,
                BriefsCreated: 0,
                CreatedTicketIds: Array.Empty<string>(),
                Failures: Array.Empty<ScaffoldFailure>(),
                WasAbortedByParseErrors: false,
                WasAbortedByValidationErrors: false,
                WasBlockedByWarnings: true,
                WasDryRun: false,
                OpTicketId: null);
        }

        // --- Step 4: dry-run ---
        if (options.DryRun)
        {
            // Count what would be created without making any API calls
            int dryPlans = opDoc.DispatchOrder.Count;
            int dryBriefs = 0;
            foreach (var entry in opDoc.DispatchOrder)
            {
                var plan = opDoc.Plans.FirstOrDefault(p => p.Id == entry.PlanId);
                if (plan != null)
                    dryBriefs += plan.Briefs.Count;
            }

            return new ScaffoldResult(
                PlansCreated: dryPlans,
                BriefsCreated: dryBriefs,
                CreatedTicketIds: Array.Empty<string>(),
                Failures: Array.Empty<ScaffoldFailure>(),
                WasAbortedByParseErrors: false,
                WasAbortedByValidationErrors: false,
                WasBlockedByWarnings: false,
                WasDryRun: true,
                OpTicketId: null);
        }

        // --- Step 5: create tickets ---
        int plansCreated = 0;
        int briefsCreated = 0;
        var createdIds = new List<string>();
        var failures = new List<ScaffoldFailure>();
        string opTicketId = string.Empty;
        string opTicketUuid = string.Empty;

        // Track plan and brief ticket IDs for dependency relation creation.
        // planIdToTicketId: Plan.Id -> human-readable ticket ID (e.g. "TLB-5")
        var planIdToTicketId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // briefKeyToTicketId: "{PlanId}:{BriefNumber}" -> human-readable ticket ID
        var briefKeyToTicketId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 5a: create operation ticket
        string opStage = "op_create";
        try
        {
            string opTitle = $"Operation: {opDoc.OperationSlug}";
            string opHtml = BriefHtmlRenderer.RenderOpDoc(opDoc);
            var opResult = await _ticketing.CreateTicketAsync(
                opTitle,
                null,
                opHtml,
                new[] { "plan-ticket" },
                ct).ConfigureAwait(false);

            opTicketId = opResult.Id;
            opTicketUuid = opResult.Uuid;
            createdIds.Add(opTicketId);

            await EmitAsync(new Dictionary<string, object>
            {
                ["action"] = "create_ticket",
                ["id"] = opTicketId,
                ["role"] = "operation"
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new ScaffoldFailure(opStage, ex.Message));
            // Can't link plans without a parent - but we still attempt plan creation
        }

        foreach (var entry in opDoc.DispatchOrder)
        {
            var plan = opDoc.Plans.FirstOrDefault(p => p.Id == entry.PlanId);
            if (plan == null)
                continue;

            // 5a: create plan-ticket
            string planTicketId = string.Empty;
            string planTicketUuid = string.Empty;
            string planStage = $"plan_{plan.Id}_create";

            try
            {
                string planTitle = $"Plan {plan.Id}: {plan.Name}";
                string planHtml = BriefHtmlRenderer.RenderPlan(plan);
                var planResult = await _ticketing.CreateTicketAsync(
                    planTitle,
                    null,
                    planHtml,
                    new[] { "plan-ticket" },
                    ct).ConfigureAwait(false);

                planTicketId = planResult.Id;
                planTicketUuid = planResult.Uuid;
                createdIds.Add(planTicketId);
                plansCreated++;
                planIdToTicketId[plan.Id] = planTicketId;

                await EmitAsync(new Dictionary<string, object>
                {
                    ["action"] = "create_ticket",
                    ["id"] = planTicketId,
                    ["role"] = "plan"
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new ScaffoldFailure(planStage, ex.Message));
                // Can't link briefs without a parent - but we still attempt brief creation
                // (orphaned briefs are visible in the list; operator cleans up)
            }

            // Set parent link: plan -> operation ticket
            if (!string.IsNullOrEmpty(opTicketUuid) && !string.IsNullOrEmpty(planTicketUuid))
            {
                string planParentStage = $"plan_{plan.Id}_parent_link";
                try
                {
                    await _ticketing.SetParentAsync(planTicketUuid, opTicketUuid, ct)
                        .ConfigureAwait(false);

                    await EmitAsync(new Dictionary<string, object>
                    {
                        ["action"] = "set_parent",
                        ["child"] = planTicketId,
                        ["parent"] = opTicketId
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(new ScaffoldFailure(planParentStage, ex.Message));
                    // Continue to next plan - parent link failure is logged, not fatal
                }
            }

            // 5b: create brief-tickets
            foreach (var brief in plan.Briefs)
            {
                string briefTitle = brief.Slug;
                string briefStageCreate = $"brief_{plan.Id}_{brief.Number:D2}_create";
                string briefTicketId = string.Empty;
                string briefTicketUuid = string.Empty;

                try
                {
                    string briefHtml = BriefHtmlRenderer.RenderBrief(brief);
                    var briefResult = await _ticketing.CreateTicketAsync(
                        briefTitle,
                        null,
                        briefHtml,
                        null,
                        ct).ConfigureAwait(false);

                    briefTicketId = briefResult.Id;
                    briefTicketUuid = briefResult.Uuid;
                    createdIds.Add(briefTicketId);
                    briefsCreated++;
                    briefKeyToTicketId[$"{plan.Id}:{brief.Number}"] = briefTicketId;

                    await EmitAsync(new Dictionary<string, object>
                    {
                        ["action"] = "create_ticket",
                        ["id"] = briefTicketId,
                        ["role"] = "brief"
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(new ScaffoldFailure(briefStageCreate, ex.Message));
                    continue; // can't set parent on a ticket that failed to create
                }

                // Set parent link
                if (!string.IsNullOrEmpty(planTicketUuid) && !string.IsNullOrEmpty(briefTicketUuid))
                {
                    string parentStage = $"brief_{plan.Id}_{brief.Number:D2}_parent_link";
                    try
                    {
                        await _ticketing.SetParentAsync(briefTicketUuid, planTicketUuid, ct)
                            .ConfigureAwait(false);

                        await EmitAsync(new Dictionary<string, object>
                        {
                            ["action"] = "set_parent",
                            ["child"] = briefTicketId,
                            ["parent"] = planTicketId
                        }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new ScaffoldFailure(parentStage, ex.Message));
                        // Continue to next brief - parent link failure is logged, not fatal
                    }
                }
            }
        }

        // --- Step 6: create blocked_by dependency relations ---
        var dependencyEdges = new List<DependencyEdge>();

        // Plan-level dependencies (Dispatch order Depends-on column)
        foreach (var entry in opDoc.DispatchOrder)
        {
            if (string.IsNullOrEmpty(entry.DependsOn) || entry.DependsOn == "-")
                continue;

            if (!planIdToTicketId.TryGetValue(entry.PlanId, out var blockedTicketId))
                continue;
            if (!planIdToTicketId.TryGetValue(entry.DependsOn, out var blockerTicketId))
                continue;

            string relStage = $"plan_{entry.PlanId}_blocked_by_{entry.DependsOn}";
            try
            {
                await _ticketing.AddRelationAsync(blockedTicketId, blockerTicketId, ct)
                    .ConfigureAwait(false);

                dependencyEdges.Add(new DependencyEdge(blockedTicketId, blockerTicketId));

                await EmitAsync(new Dictionary<string, object>
                {
                    ["action"] = "add_relation",
                    ["kind"] = "blocked_by",
                    ["blocked"] = blockedTicketId,
                    ["blocker"] = blockerTicketId
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new ScaffoldFailure(relStage, ex.Message));
            }
        }

        // Brief-level dependencies (Briefs table Deps column)
        foreach (var plan in opDoc.Plans)
        {
            foreach (var brief in plan.Briefs)
            {
                if (string.IsNullOrEmpty(brief.DependsOn) || brief.DependsOn == "-")
                    continue;

                string briefKey = $"{plan.Id}:{brief.Number}";
                if (!briefKeyToTicketId.TryGetValue(briefKey, out var blockedTicketId))
                    continue;

                // DependsOn is a comma-separated list of brief numbers
                var depParts = brief.DependsOn.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var depPart in depParts)
                {
                    if (!int.TryParse(depPart, out int depNum))
                        continue;

                    // Find which plan owns brief depNum (it must be within the same plan per op-doc format)
                    string depKey = $"{plan.Id}:{depNum}";
                    if (!briefKeyToTicketId.TryGetValue(depKey, out var blockerTicketId))
                        continue;

                    string relStage = $"brief_{plan.Id}_{brief.Number:D2}_blocked_by_{depNum:D2}";
                    try
                    {
                        await _ticketing.AddRelationAsync(blockedTicketId, blockerTicketId, ct)
                            .ConfigureAwait(false);

                        dependencyEdges.Add(new DependencyEdge(blockedTicketId, blockerTicketId));

                        await EmitAsync(new Dictionary<string, object>
                        {
                            ["action"] = "add_relation",
                            ["kind"] = "blocked_by",
                            ["blocked"] = blockedTicketId,
                            ["blocker"] = blockerTicketId
                        }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new ScaffoldFailure(relStage, ex.Message));
                    }
                }
            }
        }

        // Print dependency edge summary
        if (dependencyEdges.Count > 0)
        {
            Console.WriteLine($"[scaffold] dependency edges created: {dependencyEdges.Count}");
            foreach (var edge in dependencyEdges)
                Console.WriteLine($"  {edge.BlockerId} -> {edge.BlockedId} (blocked_by)");
        }
        else
        {
            Console.WriteLine("[scaffold] no dependency edges declared");
        }

        return new ScaffoldResult(
            PlansCreated: plansCreated,
            BriefsCreated: briefsCreated,
            CreatedTicketIds: createdIds,
            Failures: failures,
            WasAbortedByParseErrors: false,
            WasAbortedByValidationErrors: false,
            WasBlockedByWarnings: false,
            WasDryRun: false,
            OpTicketId: !string.IsNullOrEmpty(opTicketId) ? opTicketId : null,
            DependencyEdges: dependencyEdges);
    }

    private Task EmitAsync(IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        return _events.EmitAsync(new WorkflowEvent(
            _sessionId,
            DateTimeOffset.UtcNow,
            EventKind.TicketWrite,
            string.Empty,
            Phase.Scaffold,
            data), ct);
    }
}
