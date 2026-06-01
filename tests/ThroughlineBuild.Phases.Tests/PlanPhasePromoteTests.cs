using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class PlanPhasePromoteTests
{
    private const string MainSha = "deadbeef1234";

    private static Ticket MakeTicket(
        TicketState state = TicketState.Backlog,
        Size size = Size.M,
        Risk risk = Risk.Medium,
        IReadOnlyList<string>? labels = null) => new Ticket(
        Id: "TLB-99",
        Uuid: "ticket-uuid-99",
        Title: "Hand-authored ticket",
        Type: "feature",
        State: state,
        Size: size,
        Risk: risk,
        DescriptionHtml: "<h1>Goal</h1><p>Already planned.</p>",
        Relations: Array.Empty<Relation>(),
        Labels: labels ?? Array.Empty<string>(),
        ParentId: null);

    private static BuildOptions MakePromoteOptions() => new BuildOptions(
        SessionId: "session-promote",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        PromotePlan: true);

    private static BuildOptions MakeNormalOptions() => new BuildOptions(
        SessionId: "session-normal",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5));

    private static WorkerResult OkWorkerResult() => new WorkerResult(
        Status.Ok, "ok", Array.Empty<string>(), null,
        new Dictionary<string, object>
        {
            ["plan_body_ref"] = "PLAN_BODY",
            ["risk_label"] = "low",
            ["size_label"] = "S",
            ["planned_at_sha"] = "abc123"
        },
        new Dictionary<string, string> { ["PLAN_BODY"] = "# Plan" });

    [Fact]
    public async Task Promote_HappyPath_NoWorkerSpawned()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        var result = await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(worker.WasInvoked);
    }

    [Fact]
    public async Task Promote_HappyPath_DescriptionUnmodified()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Empty(ticketing.AppendDescriptions);
    }

    [Fact]
    public async Task Promote_HappyPath_PlannedAtCommentPosted()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        var result = await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Single(ticketing.Comments);
        Assert.Contains(MainSha, ticketing.Comments[0].html);
        Assert.Equal(MainSha, result.PlannedAtSha);
    }

    [Fact]
    public async Task Promote_HappyPath_ReachesReadyViaPlanningTransition()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(2, ticketing.Transitions.Count);
        Assert.Equal(TicketState.Planning, ticketing.Transitions[0].state);
        Assert.Equal(TicketState.Ready, ticketing.Transitions[1].state);
    }

    [Fact]
    public async Task Promote_HappyPath_LabelsDeriviedFromTicketEnums()
    {
        var ticketing = new FakeTicketing(MakeTicket(size: Size.L, risk: Risk.High));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        var result = await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal("high", result.RiskLabel);
        Assert.Equal("L", result.SizeLabel);
        Assert.Single(ticketing.ApplyLabels);
        Assert.Contains("risk:high", ticketing.ApplyLabels[0].labels);
        Assert.Contains("size:L", ticketing.ApplyLabels[0].labels);
    }

    [Fact]
    public async Task Promote_ParentGuard_StillFiresBeforePromote()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        ticketing.SeedChildren(new[] { MakeTicket(), MakeTicket() });
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        var result = await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("children", result.FailureReason);
        Assert.False(worker.WasInvoked);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task Promote_StateGuard_StillFires()
    {
        var ticketing = new FakeTicketing(MakeTicket(state: TicketState.Ready));
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        var result = await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Backlog", result.FailureReason);
        Assert.Empty(ticketing.Transitions);
    }

    [Fact]
    public async Task Promote_FalseByDefault_WorkerRuns()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakeNormalOptions(), git);

        await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(worker.WasInvoked);
    }

    [Fact]
    public async Task Promote_HappyPath_EmitsNoWorkerSpawnOrLlmCallEvent()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakePromoteOptions(), git);

        await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.DoesNotContain(events.Events, e => e.Kind == EventKind.WorkerSpawn);
        Assert.DoesNotContain(events.Events, e => e.Kind == EventKind.LlmCall);
    }

    [Fact]
    public async Task Default_NoFlag_EmitsWorkerSpawnEvent()
    {
        var ticketing = new FakeTicketing(MakeTicket());
        var worker = new FakeWorkerAgent(OkWorkerResult());
        var events = new FakeEventSink();
        var git = new FakeGitClient(MainSha);
        var phase = new PlanPhase(ticketing, worker, events, MakeNormalOptions(), git);

        await phase.RunAsync("TLB-99", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Contains(events.Events, e => e.Kind == EventKind.WorkerSpawn);
    }

    // --------------- Fakes ---------------

    private sealed class FakeTicketing : ITicketing
    {
        private readonly Ticket _ticket;
        private List<Ticket> _children = new();

        public List<(string id, TicketState state)> Transitions { get; } = new();
        public List<(string id, string html)> AppendDescriptions { get; } = new();
        public List<(string id, IReadOnlyList<string> labels)> ApplyLabels { get; } = new();
        public List<(string id, string html)> Comments { get; } = new();

        public FakeTicketing(Ticket ticket) { _ticket = ticket; }
        public void SeedChildren(IReadOnlyList<Ticket> children) => _children = children.ToList();

        public BackendCapabilities Capabilities => new BackendCapabilities(true, true, true, false);
        public Task<Ticket> GetAsync(string id, CancellationToken ct) => Task.FromResult(_ticket);
        public Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<Ticket>)new[] { _ticket });
        public Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(_children.AsReadOnly());
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
            return Task.FromResult("comment-promote-1");
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

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public bool WasInvoked { get; private set; }
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();
        public Task EmitAsync(WorkflowEvent ev, CancellationToken ct) { Events.Add(ev); return Task.CompletedTask; }
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
