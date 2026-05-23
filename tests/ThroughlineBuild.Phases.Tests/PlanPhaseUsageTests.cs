using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class PlanPhaseUsageTests
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
    public async Task PlanPhase_emits_llm_call_event_on_success()
    {
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Backlog));
        var llmUsage = new Dictionary<string, object>
        {
            ["input_tokens"] = 1234,
            ["output_tokens"] = 567,
            ["model"] = "claude-opus"
        };
        var metadata = new Dictionary<string, object>(ValidMetadata())
        {
            ["llm_usage"] = llmUsage
        };
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "ok", Array.Empty<string>(), null, metadata));
        var events = new FakeEventSink();
        var git = new FakeGitClient("test-sha-123");
        var phase = new PlanPhase(ticketing, worker, events, MakeOptions(), git);

        var result = await phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);

        var llmCallEvents = events.Events.Where(e => e.Kind == EventKind.LlmCall).ToList();
        Assert.Single(llmCallEvents);

        var llmEvent = llmCallEvents[0];
        Assert.Equal("TLB-1", llmEvent.TicketId);
        Assert.Equal(Phase.Plan, llmEvent.Phase);
        Assert.Equal(1234, llmEvent.Data["input_tokens"]);
        Assert.Equal(567, llmEvent.Data["output_tokens"]);
        Assert.Equal("claude-opus", llmEvent.Data["model"]);
    }

    [Fact]
    public async Task PlanPhase_omits_llm_call_event_on_worker_failure()
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

        var llmCallEvents = events.Events.Where(e => e.Kind == EventKind.LlmCall).ToList();
        Assert.Empty(llmCallEvents);
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
    }
}
