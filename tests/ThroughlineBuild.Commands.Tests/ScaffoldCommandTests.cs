using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

/// <summary>
/// Tests for ScaffoldCommand covering the 5 brief-specified cases:
/// 1. validate-only mode exits cleanly without Plane API calls
/// 2. dry-run mode previews without API calls
/// 3. accept-warnings mode proceeds past validation warnings
/// 4. validation errors cause non-zero exit (CommandResult.Success == false)
/// 5. successful scaffold prints expected output
/// </summary>
public sealed class ScaffoldCommandTests : IDisposable
{
    private readonly string _tempDir;

    public ScaffoldCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tlb-169-" + Guid.NewGuid().ToString("N"));
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

    // A minimal valid op-doc with 1 plan and 2 briefs.
    private const string ValidOpDoc = """
# Operation: test-op

Test operation for ScaffoldCommand tests.

## Why this exists

This op-doc tests the scaffold command.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Plan Alpha | - | M |

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

## What done looks like

- All components implemented and tested
- Integration test passes end to end
""";

    // An op-doc with validation errors (empty plan goal makes the validator produce errors).
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

    // An op-doc that is valid but has warnings (non-standard effort + sparse OOS).
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

    // ---- Test 1: validate-only mode makes no Plane API calls ----

    [Fact]
    public async Task ValidateOnly_NoApiCalls_ValidDoc_Succeeds()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var ctx = MakeCtx(path, validateOnly: true);
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        // validate-only on a valid doc should succeed
        Assert.True(result.Success);
        // No Plane API calls should have been made
        Assert.Equal(0, ticketing.CreateCalls);
        Assert.Equal(0, ticketing.SetParentCalls);
        // Output should mention "Validating"
        Assert.NotNull(result.Message);
        Assert.Contains("Validating", result.Message);
    }

    // ---- Test 2: dry-run mode previews without API calls ----

    [Fact]
    public async Task DryRun_NoApiCalls_PrintsPreviewTree()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var ctx = MakeCtx(path, dryRun: true, acceptWarnings: true);
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        // dry-run should succeed
        Assert.True(result.Success);
        // No Plane API calls
        Assert.Equal(0, ticketing.CreateCalls);
        Assert.Equal(0, ticketing.SetParentCalls);
        // Output should describe what would be created
        Assert.NotNull(result.Message);
        Assert.Contains("Would create", result.Message);
        // Should show plan entry
        Assert.Contains("Plan A", result.Message);
        // Should show brief entries
        Assert.Contains("alpha-one", result.Message);
        Assert.Contains("alpha-two", result.Message);
    }

    // ---- Test 3: accept-warnings mode proceeds past warnings ----

    [Fact]
    public async Task AcceptWarnings_ProceedsAndCreatesTickets()
    {
        var path = WriteOpDoc(OpDocWithWarnings);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        // Without --accept-warnings, the command should be blocked by warnings.
        var ctxBlocked = MakeCtx(path);
        var blockedResult = await cmd.ExecuteAsync(ctxBlocked, CancellationToken.None);
        Assert.False(blockedResult.Success);
        Assert.Contains("See 'build op-doc spec' for the authoring rules.", blockedResult.Message);
        Assert.Equal(0, ticketing.CreateCalls);

        // With --accept-warnings, it should proceed and create tickets.
        var ctxAccept = MakeCtx(path, acceptWarnings: true);
        var acceptResult = await cmd.ExecuteAsync(ctxAccept, CancellationToken.None);
        Assert.True(acceptResult.Success);
        // 1 op + 1 plan + 1 brief = 3 creates
        Assert.Equal(3, ticketing.CreateCalls);
    }

    // ---- Test 4: validation errors cause non-zero exit ----

    [Fact]
    public async Task ValidationErrors_ReturnFailure_WithExitCategoryTwo()
    {
        var path = WriteOpDoc(OpDocWithValidationError);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var ctx = MakeCtx(path);
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        // Should fail
        Assert.False(result.Success);
        // No API calls should have been made
        Assert.Equal(0, ticketing.CreateCalls);
        // Exit tag should be ValidationError (EXIT:2)
        Assert.NotNull(result.Message);
        Assert.StartsWith(ScaffoldExitCategory.ValidationError, result.Message);
        Assert.Contains("See 'build op-doc spec' for the authoring rules.", result.Message);
    }

    // ---- Test 5: successful scaffold prints expected output ----

    [Fact]
    public async Task SuccessPath_PrintsExpectedOutput_And_CreatesTickets()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var ctx = MakeCtx(path, acceptWarnings: true);
        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        // Should succeed
        Assert.True(result.Success);
        // 1 op + 1 plan + 2 briefs = 4 API calls
        Assert.Equal(4, ticketing.CreateCalls);
        // SetParent should be called for each brief (2) and plan to op (1) = 3 times
        Assert.Equal(3, ticketing.SetParentCalls);
        // Output should mention "Scaffolding" and "Scaffold complete"
        Assert.NotNull(result.Message);
        Assert.Contains("Scaffolding", result.Message);
        Assert.Contains("Scaffold complete", result.Message);
        // Exit tag should be Clean (EXIT:0)
        Assert.StartsWith(ScaffoldExitCategory.Clean, result.Message);
    }

    // ---- Test 6: created-ticket ids correlate to the right plan/brief (no off-by-one) ----

    [Fact]
    public async Task SuccessPath_CorrelatesTicketIdsToPlansAndBriefs_NoOffByOne()
    {
        // Fake assigns ids sequentially: op=TLB-101, plan=TLB-102, briefs=TLB-103/104.
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ScaffoldFakeTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var result = await cmd.ExecuteAsync(MakeCtx(path, acceptWarnings: true), CancellationToken.None);

        Assert.True(result.Success);
        var msg = result.Message!;

        // Operation prints its own id; the plan must NOT reuse it.
        Assert.Contains("Created operation ticket: TLB-101", msg);
        Assert.Contains("Created plan A: TLB-102", msg);
        Assert.DoesNotContain("Created plan A: TLB-101", msg);

        // Briefs map to the next two ids - and the last id is not dropped.
        Assert.Contains("Created brief: TLB-103 \"alpha-one\" (parent: TLB-102)", msg);
        Assert.Contains("Created brief: TLB-104 \"alpha-two\" (parent: TLB-102)", msg);
    }

    [Fact]
    public async Task FullBackendFailure_UsesBackendUnavailableExitCategory()
    {
        var path = WriteOpDoc(ValidOpDoc);
        var ticketing = new ThrowingCreateTicketing();
        var events = new ScaffoldFakeEventSink();
        var phase = new ScaffoldPhase(ticketing, events, "test-session");
        var cmd = new ScaffoldCommand(phase);

        var result = await cmd.ExecuteAsync(MakeCtx(path, acceptWarnings: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(ScaffoldExitCategory.BackendUnavailable, result.Message);
        Assert.Contains("Failures:", result.Message);
        Assert.DoesNotContain("Scaffold complete", result.Message);
    }

    // ---- Helper ----

    private static TicketCommandContext MakeCtx(
        string opDocPath,
        bool validateOnly = false,
        bool dryRun = false,
        bool acceptWarnings = false)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op_doc_path"] = opDocPath
        };
        if (validateOnly) args["validate_only"] = "true";
        if (dryRun) args["dry_run"] = "true";
        if (acceptWarnings) args["accept_warnings"] = "true";
        return new TicketCommandContext("", args);
    }

    // ---- Fakes specific to ScaffoldCommand tests ----

    private sealed class ScaffoldFakeTicketing : ITicketing
    {
        public int CreateCalls { get; private set; }
        public int SetParentCalls { get; private set; }

        private int _seqNum = 0;

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-1");

        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());

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
            CreateCalls++;
            _seqNum++;
            string id = $"TLB-{100 + _seqNum}";
            string uuid = $"00000000-0000-0000-0000-{_seqNum:D12}";
            return Task.FromResult(new NewTicketResult(id, uuid, DateTime.UtcNow));
        }

        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
        {
            SetParentCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
    
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) =>
            Task.CompletedTask;

    public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                children.Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}")).ToList().AsReadOnly(),
                Array.Empty<string>()));
    }

    private sealed class ThrowingCreateTicketing : ITicketing
    {
        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) => throw new NotImplementedException();
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) => throw new NotImplementedException();
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) => Task.FromResult("comment-1");
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<Relation>>(Array.Empty<Relation>());
        public Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct) => Task.CompletedTask;
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) => Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TicketComment>>(Array.Empty<TicketComment>());
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml, IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new InvalidOperationException("backend unavailable");
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) => Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(Array.Empty<CreatedChild>(), Array.Empty<string>()));
    }

    private sealed class ScaffoldFakeEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
