using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

/// <summary>
/// Pins the scaffold half of the sequence-contract (Brief 18, Plan E):
///   (1) Declared plan-level and brief-level deps round-trip to blocked_by relations.
///   (2) A brief DependsOn referencing an unknown brief number raises a validation error.
///
/// Tests are separated from ScaffoldPhaseTests to make the contract explicit.
/// The FakeTicketing here tracks AddRelationAsync calls so the edge assertions are sharp.
/// </summary>
public class SequenceContractScaffoldTests : IDisposable
{
    private readonly string _tempDir;

    public SequenceContractScaffoldTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tlb-e18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    private string WriteOpDoc(string content)
    {
        var path = Path.Combine(_tempDir, "op.md");
        File.WriteAllText(path, content);
        return path;
    }

    // An op-doc with two plans (B depends on A) and briefs with deps (02 depends on 01)
    // so both plan-level and brief-level blocked_by relations are exercised.
    private const string OpDocWithDeps = """
# Operation: seq-test

Sequence-contract test op.

## Why this exists

Tests that declared deps become blocked_by relations.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plan Alpha | - | S |
| B    | Plan Beta  | A | S |

## Plan A: Plan Alpha

### Goal

Build the first alpha component.

### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 01 | alpha-one | First alpha brief | - |
| 02 | alpha-two | Second alpha brief | 01 |

### Briefs - detail

#### Brief 01: alpha-one

Goal: Implement the first alpha component.

Inputs:
- Spec document

Outputs:
- AlphaOne module

Acceptance:
- [ ] AlphaOne exists

Notes: Keep it simple.

OOS:
- No UI
- No caching
- No config file

#### Brief 02: alpha-two

Goal: Implement the second alpha component.

Inputs:
- AlphaOne from Brief 01

Outputs:
- AlphaTwo module

Acceptance:
- [ ] AlphaTwo exists

Notes: Depends on AlphaOne.

OOS:
- No rollback
- No persistence
- No remote calls

## Plan B: Plan Beta

### Goal

Build the beta component after alpha is complete.

### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 03 | beta-one | First beta brief | - |

### Briefs - detail

#### Brief 03: beta-one

Goal: Implement the first beta component.

Inputs:
- Alpha output

Outputs:
- BetaOne module

Acceptance:
- [ ] BetaOne exists

Notes: Depends on Plan A being complete.

OOS:
- No advanced config
- No external calls
- No caching

## What done looks like

All components implemented and tested.
""";

    // ==========================================================================
    // Test 1: declared deps produce blocked_by relations
    // ==========================================================================

    /// <summary>
    /// ScaffoldPhase must create a blocked_by relation for every declared dependency:
    /// - Plan-level: Plan B is blocked by Plan A (one edge).
    /// - Brief-level: Brief 02 is blocked by Brief 01 within Plan A (one edge).
    /// Total: exactly two AddRelationAsync calls, one for each declared dep.
    /// </summary>
    [Fact]
    public async Task ScaffoldPhase_DeclaredDeps_BecomeBlockedByRelations()
    {
        var path = WriteOpDoc(OpDocWithDeps);
        var ticketing = new TrackingFakeTicketing();
        var events = new SilentEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "seq-session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // Scaffold should complete without errors.
        Assert.False(result.WasAbortedByParseErrors, "should not abort on parse errors");
        Assert.False(result.WasAbortedByValidationErrors, "should not abort on validation errors");
        Assert.False(result.WasDryRun);

        // DependencyEdges on the result must reflect both declared deps.
        Assert.NotNull(result.DependencyEdges);
        Assert.Equal(2, result.DependencyEdges!.Count);

        // AddRelationAsync must have been called exactly twice.
        Assert.Equal(2, ticketing.RelationCalls.Count);

        // Identify which ticket IDs correspond to which briefs by create order:
        // Call 1: op ticket, Call 2: Plan A, Call 3: Brief 01, Call 4: Brief 02,
        // Call 5: Plan B, Call 6: Brief 03.
        // Plan-level dep: Plan B (TLB-5) blocked by Plan A (TLB-2).
        // Brief-level dep: Brief 02 (TLB-4) blocked by Brief 01 (TLB-3).

        // Plan-level edge: planB_id blocked_by planA_id
        string planAId = "TLB-2";
        string planBId = "TLB-5";
        Assert.Contains(ticketing.RelationCalls,
            r => r.BlockedId == planBId && r.BlockerId == planAId);

        // Brief-level edge: brief02_id blocked_by brief01_id
        string brief01Id = "TLB-3";
        string brief02Id = "TLB-4";
        Assert.Contains(ticketing.RelationCalls,
            r => r.BlockedId == brief02Id && r.BlockerId == brief01Id);
    }

    /// <summary>
    /// DryRun must not call AddRelationAsync but the DependencyEdges count on the
    /// result should still reflect the preview (consistent with PlansCreated /
    /// BriefsCreated dry-run behavior). The actual requirement is that no API call
    /// is made during dry-run, so we assert zero relation calls.
    /// </summary>
    [Fact]
    public async Task ScaffoldPhase_DryRun_DoesNotCallAddRelation()
    {
        var path = WriteOpDoc(OpDocWithDeps);
        var ticketing = new TrackingFakeTicketing();
        var events = new SilentEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "seq-session-dry");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: true, AcceptWarnings: true),
            CancellationToken.None);

        Assert.True(result.WasDryRun);
        Assert.Empty(ticketing.RelationCalls);
    }

    // ==========================================================================
    // Test 2: unknown brief dep raises a validation error
    // ==========================================================================

    /// <summary>
    /// OpDocValidator must reject a brief whose DependsOn references a brief number
    /// that does not exist in the same plan. The error code is BRIEF_DEP_INVALID.
    /// </summary>
    [Fact]
    public void Validator_BriefDepReferencingUnknownBrief_RaisesValidationError()
    {
        // Brief 02 claims to depend on brief 99, which does not exist in Plan A.
        var brief1 = new Brief(
            Slug: "alpha-one", Number: 1, Title: "Alpha One",
            Goal: "Do something",
            Inputs: new[] { "input" },
            Outputs: new[] { "output" },
            AcceptanceCriteria: new[] { "it works" },
            Notes: "A note",
            OutOfScope: new[] { "OOS1", "OOS2", "OOS3" },
            DependsOn: null);

        var brief2 = new Brief(
            Slug: "alpha-two", Number: 2, Title: "Alpha Two",
            Goal: "Do something else",
            Inputs: new[] { "input" },
            Outputs: new[] { "output" },
            AcceptanceCriteria: new[] { "it also works" },
            Notes: "Another note",
            OutOfScope: new[] { "OOS1", "OOS2", "OOS3" },
            DependsOn: "99"); // 99 does not exist

        var plan = new Plan(Id: "A", Name: "Plan Alpha", Goal: "Complete X",
            Briefs: new List<Brief> { brief1, brief2 });

        var dispatchOrder = new List<DispatchEntry>
        {
            new DispatchEntry(PlanId: "A", Name: "Plan Alpha", DependsOn: null, Effort: "S")
        };

        var opDoc = new OpDoc(
            OperationSlug: "seq-test",
            Title: "Seq Test",
            Why: "Testing",
            DispatchOrder: dispatchOrder,
            Plans: new List<Plan> { plan },
            WhatDoneLooksLike: "All done");

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        var error = result.Errors.FirstOrDefault(e => e.Code == "BRIEF_DEP_INVALID");
        Assert.NotNull(error);
        Assert.Contains("99", error!.Message);
    }

    /// <summary>
    /// A brief DependsOn that references a valid sibling brief within the same plan
    /// passes validation cleanly.
    /// </summary>
    [Fact]
    public void Validator_BriefDepReferencingKnownBrief_PassesValidation()
    {
        var brief1 = new Brief(
            Slug: "alpha-one", Number: 1, Title: "Alpha One",
            Goal: "Do something",
            Inputs: new[] { "input" },
            Outputs: new[] { "output" },
            AcceptanceCriteria: new[] { "it works" },
            Notes: "A note",
            OutOfScope: new[] { "OOS1", "OOS2", "OOS3" },
            DependsOn: null);

        var brief2 = new Brief(
            Slug: "alpha-two", Number: 2, Title: "Alpha Two",
            Goal: "Do something else",
            Inputs: new[] { "input" },
            Outputs: new[] { "output" },
            AcceptanceCriteria: new[] { "it also works" },
            Notes: "Another note",
            OutOfScope: new[] { "OOS1", "OOS2", "OOS3" },
            DependsOn: "1"); // 1 is valid - brief1

        var plan = new Plan(Id: "A", Name: "Plan Alpha", Goal: "Complete X",
            Briefs: new List<Brief> { brief1, brief2 });

        var dispatchOrder = new List<DispatchEntry>
        {
            new DispatchEntry(PlanId: "A", Name: "Plan Alpha", DependsOn: null, Effort: "S")
        };

        var opDoc = new OpDoc(
            OperationSlug: "seq-test",
            Title: "Seq Test",
            Why: "Testing",
            DispatchOrder: dispatchOrder,
            Plans: new List<Plan> { plan },
            WhatDoneLooksLike: "All done");

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors.Where(e => e.Code == "BRIEF_DEP_INVALID"));
    }

    // ==========================================================================
    // Test doubles
    // ==========================================================================

    private sealed class TrackingFakeTicketing : ITicketing
    {
        private int _createCallCount;
        private int _setParentCallCount;

        /// <summary>All AddRelationAsync calls in call order: (blockedId, blockerId).</summary>
        public List<(string BlockedId, string BlockerId)> RelationCalls { get; } = new();

        public BackendCapabilities Capabilities => new(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            Task.CompletedTask;

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-1");

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct)
        {
            RelationCalls.Add((blockedId, blockerId));
            return Task.CompletedTask;
        }

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());

        public Task<NewTicketResult> CreateTicketAsync(
            string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct)
        {
            _createCallCount++;
            var uuid = $"uuid-{_createCallCount:D4}";
            var id = $"TLB-{_createCallCount}";
            return Task.FromResult(new NewTicketResult(id, uuid, DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            _setParentCallCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class SilentEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
