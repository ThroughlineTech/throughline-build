using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class DecomposePhaseTests
{
    private static Ticket MakeTicket() => new Ticket(
        Id: "TLB-1",
        Uuid: "parent-uuid-1",
        Title: "Test parent ticket",
        Type: "feature",
        State: TicketState.Backlog,
        Size: Size.M,
        Risk: Risk.Low,
        DescriptionHtml: "<p>Parent description</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-decompose",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static JsonElement MakeChildSpecsArray(int count = 2)
    {
        var items = Enumerable.Range(1, count).Select(i =>
            $"{{\"title\":\"Child {i}\",\"description\":\"Description {i}\"," +
            $"\"acceptance_criteria\":\"AC {i}\",\"size\":\"S\",\"scope_boundary\":\"Scope {i}\"}}");
        return JsonDocument.Parse($"[{string.Join(",", items)}]").RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, object> ValidMetadata(int childCount = 2) =>
        new Dictionary<string, object> { ["child_specs"] = MakeChildSpecsArray(childCount) };

    // ------------------------------------------------------------------
    // Happy path: worker returns 2+ valid child specs -> Success=true
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_HappyPath_TwoChildSpecs_ReturnsSuccess()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("TLB-1", result.TicketId);
        Assert.NotNull(result.ChildSpecs);
        Assert.Equal(2, result.ChildSpecs!.Count);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.CreatedIds);
        Assert.Equal(2, result.CreatedIds!.Count);
    }

    [Fact]
    public async Task RunAsync_HappyPath_ThreeChildSpecs_ReturnsAllSpecs()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(3)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.ChildSpecs!.Count);
        Assert.Equal("Child 1", result.ChildSpecs[0].Title);
        Assert.Equal("Child 2", result.ChildSpecs[1].Title);
        Assert.Equal("Child 3", result.ChildSpecs[2].Title);
        Assert.NotNull(result.CreatedIds);
        Assert.Equal(3, result.CreatedIds!.Count);
    }

    [Fact]
    public async Task RunAsync_HappyPath_ChildSpecFieldsPopulated()
    {
        var json = """
            [
              {
                "title":"Auth module",
                "description":"Handles authentication",
                "acceptance_criteria":"Must pass all auth tests",
                "size":"M",
                "scope_boundary":"No authorization logic"
              },
              {
                "title":"Authz module",
                "description":"Handles authorization",
                "acceptance_criteria":"Must pass all authz tests",
                "size":"S",
                "scope_boundary":"No authentication logic"
              }
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        var first = result.ChildSpecs![0];
        Assert.Equal("Auth module", first.Title);
        Assert.Equal("Handles authentication", first.Description);
        Assert.Equal("Must pass all auth tests", first.AcceptanceCriteria);
        Assert.Equal("M", first.Size);
        Assert.Equal("No authorization logic", first.ScopeBoundary);
        Assert.NotNull(result.CreatedIds);
        Assert.Equal(2, result.CreatedIds!.Count);
    }

    // ------------------------------------------------------------------
    // Malformed output: missing child_specs key -> failure, no crash
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_MetadataMissingChildSpecsKey_ReturnsFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, new Dictionary<string, object>()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.ChildSpecs);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("child_specs", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ChildSpecsIsNotArray_ReturnsFailure()
    {
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse("\"not an array\"").RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Fewer than 2 child specs -> failure, no crash
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ChildSpecsArrayHasOneItem_ReturnsFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(1)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.ChildSpecs);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_ChildSpecsArrayIsEmpty_ReturnsFailure()
    {
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse("[]").RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Malformed element: missing title -> element is skipped, may fall below 2
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ChildSpecElementMissingTitle_SkippedAndMayFailIfBelowTwo()
    {
        var json = """
            [
              {"description":"no title here","acceptance_criteria":"AC","size":"S","scope_boundary":"none"},
              {"title":"Valid","description":"Has title","acceptance_criteria":"AC","size":"S","scope_boundary":"none"}
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Worker returns non-Ok status -> failure propagated, no crash
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_WorkerReturnsFailed_ReturnsFailureWithReason()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Failed, "boom", Array.Empty<string>(), "subprocess crashed",
            new Dictionary<string, object>()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("subprocess crashed", result.FailureReason);
        Assert.Null(result.ChildSpecs);
    }

    [Fact]
    public async Task RunAsync_WorkerReturnsNeedsRework_ReturnsFailure()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.NeedsRework, "rework", Array.Empty<string>(), "needs revision",
            new Dictionary<string, object>()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    // ------------------------------------------------------------------
    // No state transitions occur (read-only with respect to Plane)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_HappyPath_NoPlaneStateTransitions()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Empty(ticketing.Transitions);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(ticketing.ApplyLabels);
    }

    [Fact]
    public async Task RunAsync_HappyPath_PostsDecomposedAtComment()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Single(ticketing.Comments);
        var comment = ticketing.Comments[0];
        Assert.Equal("TLB-1", comment.id);
        Assert.Contains("[decomposed_at:", comment.html);
    }

    [Fact]
    public async Task RunAsync_HappyPath_CreatedChildIdsPopulated()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.CreatedIds);
        Assert.Equal(2, result.CreatedIds!.Count);
    }

    [Fact]
    public async Task RunAsync_ParentRemainsBacklog_NoTransitionCalled()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Empty(ticketing.Transitions);
    }

    // ------------------------------------------------------------------
    // Phase property
    // ------------------------------------------------------------------

    [Fact]
    public void Phase_ReturnsDecompose()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, new Dictionary<string, object>()));
        var phase = new DecomposePhase(ticketing, worker, new FakeEventSink(), MakeOptions(), new FakeGitClient("sha"));

        Assert.Equal(Phase.Decompose, phase.Phase);
    }

    // ------------------------------------------------------------------
    // IWorkflowPhase.RunAsync maps to PhaseResult with Phase.Decompose
    // ------------------------------------------------------------------

    [Fact]
    public async Task IWorkflowPhase_RunAsync_HappyPath_ReturnsPhaseResultWithDecompose()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        IWorkflowPhase phase = new DecomposePhase(
            ticketing, worker, new FakeEventSink(), MakeOptions(), new FakeGitClient("sha"));

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Phase.Decompose, result.Phase);
        Assert.True(result.Outputs.TryGetValue("child_spec_count", out var count));
        Assert.Equal("2", count);
        Assert.True(result.Outputs.TryGetValue("created_child_count", out var createdCount));
        Assert.Equal("2", createdCount);
    }

    [Fact]
    public async Task IWorkflowPhase_RunAsync_Failure_ReturnsPhaseResultWithDecompose()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Failed, "fail", Array.Empty<string>(), "bad output",
            new Dictionary<string, object>()));
        IWorkflowPhase phase = new DecomposePhase(
            ticketing, worker, new FakeEventSink(), MakeOptions(), new FakeGitClient("sha"));

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(Phase.Decompose, result.Phase);
        Assert.Equal("bad output", result.FailureReason);
        Assert.Empty(result.Outputs);
    }

    // ------------------------------------------------------------------
    // Verdict check integration: fails when coverage_check fails
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_VerdictFailure_MissingScope_ReturnsFailure()
    {
        var json = """
            [
              {
                "title":"Child 1",
                "description":"Desc 1",
                "acceptance_criteria":"AC 1",
                "size":"S",
                "scope_boundary":"Scope 1"
              },
              {
                "title":"Child 2",
                "description":"Desc 2",
                "acceptance_criteria":"AC 2",
                "size":"M",
                "scope_boundary":""
              }
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.VerdictFailures);
        Assert.NotEmpty(result.VerdictFailures!);
        Assert.Single(result.VerdictFailures.Where(f => f.Contains("coverage_check")));
        Assert.NotNull(result.FailureReason);
        Assert.Contains("coverage_check", result.FailureReason);
        // No child tickets created on verdict failure
        Assert.Empty(ticketing.CreateChildTicketsCalls);
    }

    [Fact]
    public async Task RunAsync_VerdictFailure_DuplicateTitle_ReturnsFailure()
    {
        var json = """
            [
              {
                "title":"Auth",
                "description":"Desc 1",
                "acceptance_criteria":"AC 1",
                "size":"S",
                "scope_boundary":"Scope 1"
              },
              {
                "title":"Auth",
                "description":"Desc 2",
                "acceptance_criteria":"AC 2",
                "size":"M",
                "scope_boundary":"Scope 2"
              }
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.VerdictFailures);
        Assert.Single(result.VerdictFailures.Where(f => f.Contains("uniqueness_check")));
        Assert.Empty(ticketing.CreateChildTicketsCalls);
    }

    [Fact]
    public async Task RunAsync_VerdictFailure_BadSize_ReturnsFailure()
    {
        var json = """
            [
              {
                "title":"Child 1",
                "description":"Desc 1",
                "acceptance_criteria":"AC 1",
                "size":"XL",
                "scope_boundary":"Scope 1"
              },
              {
                "title":"Child 2",
                "description":"Desc 2",
                "acceptance_criteria":"AC 2",
                "size":"M",
                "scope_boundary":"Scope 2"
              }
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.VerdictFailures);
        Assert.Single(result.VerdictFailures.Where(f => f.Contains("size_check")));
        Assert.Empty(ticketing.CreateChildTicketsCalls);
    }

    [Fact]
    public async Task RunAsync_VerdictPass_CreatesChildTickets()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.VerdictFailures);
        Assert.Empty(result.VerdictFailures!);
        // Child tickets created successfully
        Assert.Single(ticketing.CreateChildTicketsCalls);
        Assert.Equal(2, ticketing.CreateChildTicketsCalls[0].children.Count);
    }

    [Fact]
    public async Task RunAsync_VerdictPass_EmitsPassedEvent()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata(2)));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        // Look for VerifierVerdict event with status VerdictPassed
        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.NotEmpty(verdictEvents);
        var lastVerdictEvent = verdictEvents.Last();
        Assert.True(lastVerdictEvent.Data.TryGetValue("status", out var status));
        Assert.Equal("VerdictPassed", status.ToString());
    }

    [Fact]
    public async Task RunAsync_VerdictFail_EmitsFailedEvent()
    {
        var json = """
            [
              {
                "title":"Child 1",
                "description":"Desc 1",
                "acceptance_criteria":"AC 1",
                "size":"S",
                "scope_boundary":""
              },
              {
                "title":"Child 2",
                "description":"Desc 2",
                "acceptance_criteria":"AC 2",
                "size":"M",
                "scope_boundary":"Scope 2"
              }
            ]
            """;
        var metadata = new Dictionary<string, object>
        {
            ["child_specs"] = JsonDocument.Parse(json).RootElement.Clone()
        };
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("abc123");
        var phase = new DecomposePhase(ticketing, worker, events, MakeOptions(), git);

        await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        // Look for VerifierVerdict event with status VerdictFailed
        var verdictEvents = events.Events.Where(e => e.Kind == EventKind.VerifierVerdict).ToList();
        Assert.NotEmpty(verdictEvents);
        var lastVerdictEvent = verdictEvents.Last();
        Assert.True(lastVerdictEvent.Data.TryGetValue("status", out var status));
        Assert.Equal("VerdictFailed", status.ToString());
        Assert.True(lastVerdictEvent.Data.TryGetValue("checks_failed", out var failures));
        Assert.NotNull(failures);
    }

    // ------------------------------------------------------------------
    // Private fakes
    // ------------------------------------------------------------------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> AppendDescriptions { get; } = new();
        public List<(string id, IReadOnlyList<string> labels)> ApplyLabels { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();
        public List<(string parentUuid, IReadOnlyList<ChildTicketSpec> children)> CreateChildTicketsCalls { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
        {
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }
        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
        {
            AppendDescriptions.Add((id, html));
            return Task.CompletedTask;
        }
        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
        {
            Comments.Add((id, html));
            return Task.FromResult("comment-1");
        }
        public Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
        {
            ApplyLabels.Add((id, labels.ToList()));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Relation>)Array.Empty<Relation>());
        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));
        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<TicketComment>)Array.Empty<TicketComment>());
        public Task<NewTicketResult> CreateTicketAsync(string title, string? type, string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());
        public Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct) =>
            Task.CompletedTask;
        public Task UpdateDescriptionAsync(string id, string html, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid, IReadOnlyList<ChildTicketSpec> children, CancellationToken ct)
        {
            CreateChildTicketsCalls.Add((parentUuid, children));
            var created = children
                .Select((c, i) => new CreatedChild($"fake-id-{i}", $"fake-uuid-{i}"))
                .ToList()
                .AsReadOnly();
            return Task.FromResult(new CreateChildTicketsResult(created, Array.Empty<string>()));
        }
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(_result);
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

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _sha;
        public FakeGitClient(string sha) { _sha = sha; }
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_sha);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult("0000000000000000000000000000000000000000");
        public Task<GitDiff> DiffAsync(string fromRef, string toRef, string mainWorktreePath, bool includePatchContent, CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));
        public Task<GitOpResult> FetchAsync(string remote, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<RebaseResult> RebaseAsync(string ontoRef, string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new RebaseResult(true, false, Array.Empty<string>(), null));
        public Task<GitOpResult> RebaseAbortAsync(string featureWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<GitOpResult> FastForwardMergeAsync(string mergeRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
    }
}
