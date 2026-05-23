using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class PlanPhaseTests
{
    private static Ticket MakeTicket(TicketState state) => new Ticket(
        Id: "TLB-1",
        Title: "Test ticket",
        Type: "feature",
        State: state,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>desc</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-1",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static IReadOnlyDictionary<string, object> ValidMetadata() => new Dictionary<string, object>
    {
        ["plan_html"] = "<p>plan</p>",
        ["risk_label"] = "low",
        ["size_label"] = "S",
        ["planned_at_sha"] = "abc123"
    };

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsSuccessAndWritesAllToPlane()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("test-sha-123");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("low", result.RiskLabel);
        Assert.Equal("S", result.SizeLabel);
        Assert.Equal("abc123", result.PlannedAtSha);
        Assert.Null(result.FailureReason);

        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.Planning, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.Ready, ticketing.Transitions[1].state);

        Assert.Single(ticketing.AppendDescriptions);
        Assert.Equal("<p>plan</p>", ticketing.AppendDescriptions[0].html);

        Assert.Single(ticketing.ApplyLabels);
        Assert.Contains("risk:low", ticketing.ApplyLabels[0].labels);
        Assert.Contains("size:S", ticketing.ApplyLabels[0].labels);

        Assert.Single(ticketing.Comments);
        Assert.Contains("abc123", ticketing.Comments[0].html);

        var ticketWrites = events.Events.Where(e => e.Kind == EventKind.TicketWrite).ToList();
        Assert.Equal(3, ticketWrites.Count);
    }

    [Fact]
    public async Task RunAsync_TicketNotInBacklog_ReturnsFailureImmediately()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, ValidMetadata()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("test-sha-123");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Backlog", result.FailureReason);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task RunAsync_WorkerReturnsFailed_ReturnsFailureNoPlaneWrites()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Failed, "boom", Array.Empty<string>(), "worker failed",
            new Dictionary<string, object>()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("test-sha-123");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(ticketing.AppendDescriptions);
        Assert.Empty(ticketing.ApplyLabels);
        Assert.Empty(ticketing.Comments);
        Assert.DoesNotContain(ticketing.Transitions, t => t.state == TicketState.Ready);
    }

    [Fact]
    public async Task RunAsync_WorkerReturnsEscalate_ReturnsFailureTicketLeftInPlanning()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Escalate, "escalate", Array.Empty<string>(), "needs human",
            new Dictionary<string, object>()));
        var events = new FakeEventSink();
        var git = new FakeGitClient("test-sha-123");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(ticketing.Transitions);
        Assert.Equal(TicketState.Planning, ticketing.Transitions[0].state);
    }

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> AppendDescriptions { get; } = new();
        public List<(string id, IReadOnlyList<string> labels)> ApplyLabels { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

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
    }

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "fake";
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
    }
}
