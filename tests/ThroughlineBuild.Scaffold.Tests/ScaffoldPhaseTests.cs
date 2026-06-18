using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

/// <summary>
/// Tests for ScaffoldPhase.RunAsync.
/// Uses a temporary file on disk for the op-doc path to exercise the real parser.
/// </summary>
public class ScaffoldPhaseTests : IDisposable
{
    private readonly string _tempDir;

    public ScaffoldPhaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tlb-167-" + Guid.NewGuid().ToString("N"));
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

    // A minimal but valid op-doc with two plans and multiple briefs
    private const string ValidOpDoc = """
# Operation: test-op

Test operation for ScaffoldPhase tests.

## Why this exists

This op-doc tests the scaffold phase wiring.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plan Alpha | - | M |
| B    | Plan Beta  | A | S |

## Plan A: Plan Alpha

### Goal

Build the alpha components.

### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 01 | alpha-one | First alpha brief | - |
| 02 | alpha-two | Second alpha brief | 01 |

### Briefs - detail

#### Brief 01: alpha-one

Goal: Implement the first alpha component.

Inputs:
- Specification document

Outputs:
- AlphaOne class

Acceptance:
- [ ] AlphaOne exists
- [ ] Tests pass

Notes: Keep it simple.

OOS:
- No advanced features
- No UI integration
- No configuration file support

#### Brief 02: alpha-two

Goal: Implement the second alpha component.

Inputs:
- AlphaOne from Brief 01

Outputs:
- AlphaTwo class

Acceptance:
- [ ] AlphaTwo exists
- [ ] Tests pass

Notes: Depends on AlphaOne.

OOS:
- No rollback support
- No persistence layer
- No remote calls

## Plan B: Plan Beta

### Goal

Build the beta components.

### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 01 | beta-one | First beta brief | - |

### Briefs - detail

#### Brief 01: beta-one

Goal: Implement the first beta component.

Inputs:
- Beta specification

Outputs:
- BetaOne class

Acceptance:
- [ ] BetaOne exists
- [ ] Tests pass

Notes: Simple implementation first.

OOS:
- No advanced configuration
- No external service calls
- No caching

## What done looks like

- All components implemented and tested
- Integration test passes end to end
""";

    // An op-doc with a validation error (empty plan goal)
    private const string OpDocWithValidationError = """
# Operation: bad-op

Bad operation.

## Why this exists

Testing validation errors.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plan Alpha | - | M |

## Plan A: Plan Alpha

### Goal



### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 01 | test-brief | Test brief | - |

### Briefs - detail

#### Brief 01: test-brief

Goal: Do something.

Inputs:
- Some input

Outputs:
- Some output

Acceptance:
- [ ] It works

Notes: Simple.

OOS:
- Not much
- Not this
- Not that

## What done looks like

- Done
""";

    // An op-doc that is valid but has warnings (non-standard effort + sparse OOS)
    private const string OpDocWithWarnings = """
# Operation: warn-op

Warning operation.

## Why this exists

Testing warnings.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plan Alpha | - | XL |

## Plan A: Plan Alpha

### Goal

Build something.

### Briefs

| # | Slug | Intent | Deps |
|---|------|--------|------|
| 01 | test-brief | Test brief | - |

### Briefs - detail

#### Brief 01: test-brief

Goal: Do something.

Inputs:
- Some input

Outputs:
- Some output

Acceptance:
- [ ] It works

Notes: A note.

OOS:
- Only one thing

## What done looks like

- Done
""";

    [Fact]
    public async Task HappyPath_CreatesExpectedCountOfTickets()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // 1 op + 2 plans + 3 briefs (2 in Plan A, 1 in Plan B) = 6 creates
        Assert.False(result.WasAbortedByParseErrors);
        Assert.False(result.WasAbortedByValidationErrors);
        Assert.False(result.WasBlockedByWarnings);
        Assert.False(result.WasDryRun);
        Assert.Equal(2, result.PlansCreated);
        Assert.Equal(3, result.BriefsCreated);
        Assert.Equal(6, result.CreatedTicketIds.Count);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task HappyPath_EmitsCreateAndParentEvents()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // 6 create_ticket events (1 op + 2 plans + 3 briefs) + 5 set_parent events (2 plan->op + 3 brief->plan) = 11 total
        var createEvents = events.Events
            .Where(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "create_ticket")
            .ToList();
        var parentEvents = events.Events
            .Where(e => e.Data.TryGetValue("action", out var a) && a.ToString() == "set_parent")
            .ToList();

        Assert.Equal(6, createEvents.Count);
        Assert.Equal(5, parentEvents.Count);
        Assert.All(events.Events, e => Assert.Equal(EventKind.TicketWrite, e.Kind));
    }

    [Fact]
    public async Task HappyPath_PlansAreTitledCorrectly()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // 3 creates with plan-ticket label: 1 operation + 2 plans
        var planCreates = ticketing.Calls
            .Where(c => c.LabelNames != null && c.LabelNames.Contains("plan-ticket"))
            .ToList();

        Assert.Equal(3, planCreates.Count);
        Assert.Contains(planCreates, c => c.Title == "Plan A: Plan Alpha");
        Assert.Contains(planCreates, c => c.Title == "Plan B: Plan Beta");
    }

    [Fact]
    public async Task HappyPath_BriefTicketsCarrySizeLabelFromPlanEffort()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // Plan A is Effort M -> both its briefs get size:m; Plan B is Effort S -> its brief gets size:s.
        // The size label is the only label on a brief (briefs never carry plan-ticket).
        var briefCreates = ticketing.Calls
            .Where(c => c.LabelNames != null
                && c.LabelNames.Any(l => l.StartsWith("size:", StringComparison.Ordinal)))
            .ToList();
        Assert.Equal(3, briefCreates.Count);

        string SizeOf(string slug) => briefCreates.Single(c => c.Title == slug).LabelNames!.Single();
        Assert.Equal("size:m", SizeOf("alpha-one"));
        Assert.Equal("size:m", SizeOf("alpha-two"));
        Assert.Equal("size:s", SizeOf("beta-one"));
    }

    [Fact]
    public async Task NonStandardEffort_DefaultsBriefToSizeM_AndEmitsWarningEvent()
    {
        // OpDocWithWarnings declares Plan A with the non-standard effort "XL".
        var path = WriteOpDoc(OpDocWithWarnings);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        var briefCreate = ticketing.Calls.Single(c => c.LabelNames != null
            && c.LabelNames.Any(l => l.StartsWith("size:", StringComparison.Ordinal)));
        Assert.Equal("size:m", briefCreate.LabelNames!.Single());

        var warning = events.Events.Single(e =>
            e.Data.TryGetValue("action", out var a) && a.ToString() == "size_label_defaulted");
        Assert.Equal("XL", warning.Data["effort"].ToString());
        Assert.Equal("size:m", warning.Data["applied"].ToString());
    }

    [Fact]
    public async Task HappyPath_ParentLinksUsePlanUuid()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // SetParentAsync should have been called 5 times (2 plans + 3 briefs)
        Assert.Equal(5, ticketing.ParentLinks.Count);
        // All parent UUIDs should be valid (non-empty)
        Assert.All(ticketing.ParentLinks, link =>
        {
            Assert.False(string.IsNullOrEmpty(link.ChildUuid));
            Assert.False(string.IsNullOrEmpty(link.ParentUuid));
        });
    }

    [Fact]
    public async Task HappyPath_PlanTicketsParentedToOpTicket()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // Operation ticket should exist and be in CreatedTicketIds
        Assert.NotNull(result.OpTicketId);
        Assert.NotEmpty(result.OpTicketId);
        Assert.Contains(result.OpTicketId, result.CreatedTicketIds);

        // First two ParentLinks should be plan->op (plans 1 and 2 parented to op)
        // The op ticket UUID is "uuid-0001" (first create)
        var opUuid = "uuid-0001";
        var planParentLinks = ticketing.ParentLinks.Where(l => l.ParentUuid == opUuid).ToList();
        Assert.Equal(2, planParentLinks.Count);
    }

    [Fact]
    public async Task ValidationErrors_AbortsBeforeAnyApiCall()
    {
        var path = WriteOpDoc(OpDocWithValidationError);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        Assert.True(result.WasAbortedByValidationErrors);
        Assert.Equal(0, result.PlansCreated);
        Assert.Equal(0, result.BriefsCreated);
        Assert.Empty(ticketing.Calls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task ParseErrors_AbortsBeforeAnyApiCall()
    {
        var path = WriteOpDoc("This is not a valid op-doc file.");
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        Assert.True(result.WasAbortedByParseErrors);
        Assert.Equal(0, result.PlansCreated);
        Assert.Empty(ticketing.Calls);
    }

    [Fact]
    public async Task Warnings_AcceptWarningsFalse_BlocksBeforeAnyApiCall()
    {
        var path = WriteOpDoc(OpDocWithWarnings);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: false),
            CancellationToken.None);

        Assert.True(result.WasBlockedByWarnings);
        Assert.Equal(0, result.PlansCreated);
        Assert.Empty(ticketing.Calls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Warnings_AcceptWarningsTrue_Proceeds()
    {
        var path = WriteOpDoc(OpDocWithWarnings);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        Assert.False(result.WasBlockedByWarnings);
        Assert.Equal(1, result.PlansCreated);
        Assert.Equal(1, result.BriefsCreated);
    }

    [Fact]
    public async Task DryRun_ReturnsPreviewCounts_NoApiCalls()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new FakeTicketing();
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: true, AcceptWarnings: true),
            CancellationToken.None);

        Assert.True(result.WasDryRun);
        Assert.Equal(2, result.PlansCreated);
        Assert.Equal(3, result.BriefsCreated);
        Assert.Empty(ticketing.Calls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task ConnectivityFailure_AbortsBeforeAnyCreate()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ConnectivityFailingTicketing("not authorized");
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        Assert.False(result.WasDryRun);
        Assert.Equal(0, result.PlansCreated);
        Assert.Equal(0, result.BriefsCreated);
        Assert.Empty(result.CreatedTicketIds);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("connectivity_check", failure.Stage);
        Assert.Contains("not authorized", failure.Detail);
        Assert.Equal(0, ticketing.CreateCalls);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task BriefCreateFailure_ContinuesToNextBrief_RecordsFailure()
    {
        var path = WriteOpDoc(ValidOpDoc);
        // Fail the third create call (first brief in plan A)
        // Call 1: op ticket, Call 2: plan A, Call 3: brief A.01 (fails)
        var ticketing = new FakeTicketing(throwOnCallNumber: 3);
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // One brief creation failed; the rest should still have been attempted
        Assert.Single(result.Failures);
        Assert.Contains("brief_A_01_create", result.Failures[0].Stage);
        // Remaining briefs in plan A (brief 02) and plan B (brief 01) should still be created
        Assert.Equal(2, result.PlansCreated);
        Assert.Equal(2, result.BriefsCreated);
    }

    [Fact]
    public async Task SetParentFailure_ContinuesToNextBrief_RecordsFailure()
    {
        var path = WriteOpDoc(ValidOpDoc);
        // Ticketing creates succeed; parent link throws on first call (first plan->op link)
        var ticketing = new FakeTicketing(throwSetParentOnCallNumber: 1);
        var events = new FakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "session-1");

        var result = await phase.RunAsync(
            new ScaffoldOptions(path, DryRun: false, AcceptWarnings: true),
            CancellationToken.None);

        // All creates succeed, one parent link fails
        Assert.Single(result.Failures);
        Assert.Contains("parent_link", result.Failures[0].Stage);
        Assert.Equal(2, result.PlansCreated);
        Assert.Equal(3, result.BriefsCreated);
        // Other parent links still proceeded: 1 remaining plan->op + 3 brief->plan = 4 total
        Assert.Equal(4, ticketing.ParentLinks.Count);
    }

    // ---- Test doubles ----

    private record CreateCall(
        string Title,
        string? Type,
        string DescriptionHtml,
        IReadOnlyList<string>? LabelNames);

    private record ParentLink(string ChildUuid, string ParentUuid);

    private sealed class FakeTicketing : ITicketing
    {
        private int _createCallCount;
        private int _setParentCallCount;
        private readonly int _throwOnCallNumber;
        private readonly int _throwSetParentOnCallNumber;

        public List<CreateCall> Calls { get; } = new();
        public List<ParentLink> ParentLinks { get; } = new();

        public FakeTicketing(int throwOnCallNumber = -1, int throwSetParentOnCallNumber = -1)
        {
            _throwOnCallNumber = throwOnCallNumber;
            _throwSetParentOnCallNumber = throwSetParentOnCallNumber;
        }

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

        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct)
        {
            _createCallCount++;
            if (_throwOnCallNumber > 0 && _createCallCount == _throwOnCallNumber)
                throw new InvalidOperationException($"Simulated create failure on call {_createCallCount}");

            Calls.Add(new CreateCall(title, type, descriptionHtml, initialLabelNames));
            var uuid = $"uuid-{_createCallCount:D4}";
            var id = $"TLB-{_createCallCount}";
            return Task.FromResult(new NewTicketResult(id, uuid, DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            _setParentCallCount++;
            if (_throwSetParentOnCallNumber > 0 && _setParentCallCount == _throwSetParentOnCallNumber)
                throw new InvalidOperationException($"Simulated SetParent failure on call {_setParentCallCount}");

            ParentLinks.Add(new ParentLink(childUuid, parentUuid));
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

    private sealed class ConnectivityFailingTicketing : ITicketing, ITicketingConnectivity
    {
        private readonly string _message;

        public ConnectivityFailingTicketing(string message)
        {
            _message = message;
        }

        public int CreateCalls { get; private set; }

        public BackendCapabilities Capabilities => new(true, true, true, false);

        public Task<TicketingConnectivityResult> TestConnectivityAsync(CancellationToken ct) =>
            Task.FromResult(new TicketingConnectivityResult(false, _message));

        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => Task.CompletedTask;
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => Task.FromResult("comment-1");
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Task.CompletedTask;
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());

        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct)
        {
            CreateCalls++;
            return Task.FromResult(new NewTicketResult("TLB-1", "uuid-1", DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(Array.Empty<CreatedChild>(), Array.Empty<string>()));
    }

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
