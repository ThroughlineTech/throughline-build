using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class BatchImplementRunnerTests
{
    private const string MainSha = "0000000000000000000000000000000000000000";
    private const string FirstSha = "1111111111111111111111111111111111111111";
    private const string SecondSha = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task RunBatchImplementSessionAsync_DispatchesOneWorkerAndAdvancesEveryTicket()
    {
        var tickets = new[] { MakeTicket("TLB-2"), MakeTicket("TLB-3") };
        var worker = new FakeWorker(new WorkerResult(
            Status.Ok,
            "batch complete",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>(),
            Tickets: new[]
            {
                new BatchTicketResult(
                    "TLB-2", FirstSha, 0, Array.Empty<string>(), "SUMMARY_1"),
                new BatchTicketResult(
                    "TLB-3", SecondSha, 1, Array.Empty<string>(), "SUMMARY_2")
            }));
        var git = new FakeGit { LogShas = new[] { SecondSha, FirstSha } };
        var ticketing = new FakeTicketing();
        var runner = BuildRunner(worker, git, ticketing);

        var outcome = await runner.RunBatchImplementSessionAsync(
            new ChainPhaseOptions("TLB-1", false),
            tickets,
            CreateTempDirectory(),
            "chain/tlb-1",
            null,
            CancellationToken.None);

        Assert.Equal(1, worker.CallCount);
        Assert.Equal(2, outcome.Results.Count);
        Assert.All(outcome.Results, result =>
            Assert.Equal(ChainOutcome.BatchImplemented, result.Outcome));
        Assert.Equal(2, outcome.ConfirmedTickets?.Count);
        Assert.Equal(
            new[]
            {
                ("TLB-2", TicketState.InProgress),
                ("TLB-3", TicketState.InProgress),
                ("TLB-2", TicketState.InReview),
                ("TLB-3", TicketState.InReview)
            },
            ticketing.Transitions);
        Assert.Equal(2, ticketing.Comments.Count);
    }

    [Fact]
    public async Task RunBatchImplementSessionAsync_TicketingOutageDegradesAtActiveTicket()
    {
        var tickets = new[] { MakeTicket("TLB-2"), MakeTicket("TLB-3") };
        var worker = new FakeWorker(new WorkerResult(
            Status.Ok,
            "should not run",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>()));
        var git = new FakeGit();
        var ticketing = new FakeTicketing { UnavailableTicketId = "TLB-3" };
        var runner = BuildRunner(worker, git, ticketing);

        var outcome = await runner.RunBatchImplementSessionAsync(
            new ChainPhaseOptions("TLB-1", false),
            tickets,
            CreateTempDirectory(),
            "chain/tlb-1",
            null,
            CancellationToken.None);

        Assert.Equal(0, worker.CallCount);
        Assert.Equal(
            ChainOutcome.TicketingUnavailable,
            Assert.Single(outcome.Results, result => result.TicketId == "TLB-3").Outcome);
        Assert.Equal(
            ChainOutcome.Skipped,
            Assert.Single(outcome.Results, result => result.TicketId == "TLB-2").Outcome);
    }

    private static BatchImplementRunner BuildRunner(
        IWorkerAgent worker,
        IGitClient git,
        ITicketing ticketing)
    {
        var baseOptions = new BuildOptions(
            SessionId: "base",
            WorkerName: "fake",
            WorkerTimeout: TimeSpan.FromMinutes(1));
        var events = new RecordingEventSink();
        var session = 0;
        return new BatchImplementRunner(
            worker,
            ticketing,
            git,
            baseOptions,
            CreateTempDirectory(),
            () => $"session-{++session}",
            sessionId => new ChainEventEmitter(events, ticketing, sessionId),
            _ => throw new InvalidOperationException("plan factory not used"),
            (sessionId, _, _, _, targetBranch) => baseOptions with
            {
                SessionId = sessionId,
                TargetBranch = targetBranch ?? baseOptions.TargetBranch
            });
    }

    private static Ticket MakeTicket(string id) => new(
        Id: id,
        Uuid: $"uuid-{id}",
        Title: $"Ticket {id}",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: "TLB-1");

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "tlb-batch-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeWorker(WorkerResult result) : IWorkerAgent
    {
        public int CallCount { get; private set; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(
            Brief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingEventSink : IEventSink
    {
        public Task EmitAsync(WorkflowEvent workflowEvent, CancellationToken ct) =>
            Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGit : IGitClient
    {
        public IReadOnlyList<string> LogShas { get; init; } = Array.Empty<string>();

        public Task<string> RevParseAsync(
            string refspec,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path,
            bool force,
            CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(
            string pattern,
            string baseBranch,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(SecondSha);

        public Task<GitDiff> DiffAsync(
            string fromRef,
            string toRef,
            string mainWorktreePath,
            bool includePatchContent,
            CancellationToken ct) =>
            Task.FromResult(new GitDiff(fromRef, toRef, Array.Empty<DiffEntry>()));

        public Task<GitOpResult> FetchAsync(
            string remote,
            string mainWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<RebaseResult> RebaseAsync(
            string ontoRef,
            string featureWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new RebaseResult(
                true, false, Array.Empty<string>(), null));

        public Task<GitOpResult> RebaseAbortAsync(
            string featureWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> FastForwardMergeAsync(
            string mergeRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> DeleteBranchAsync(
            string branch,
            bool force,
            string mainWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<int> RevListCountAsync(
            string range,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<string>> LogOnelineAsync(
            string range,
            int limit,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<GitOpResult> CreateBranchAsync(
            string branch,
            string fromRef,
            string worktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> LogShasAsync(
            string range,
            int limit,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(LogShas);
    }

    private sealed class FakeTicketing : ITicketing
    {
        public string? UnavailableTicketId { get; init; }
        public List<(string TicketId, TicketState State)> Transitions { get; } = new();
        public List<(string TicketId, string Html)> Comments { get; } = new();
        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task TransitionAsync(
            string id,
            TicketState newState,
            CancellationToken ct)
        {
            if (string.Equals(id, UnavailableTicketId, StringComparison.Ordinal))
            {
                throw new TicketingUnavailableException(
                    "ticketing unavailable",
                    new IOException("connection refused"));
            }
            Transitions.Add((id, newState));
            return Task.CompletedTask;
        }

        public Task<string> CreateCommentAsync(
            string id,
            string html,
            CancellationToken ct)
        {
            Comments.Add((id, html));
            return Task.FromResult("comment-id");
        }

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AppendDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ApplyLabelsAsync(
            string id,
            IEnumerable<string> labels,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(
            string id,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddRelationAsync(
            string blockedId,
            string blockerId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RollupResult> RollupParentAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(
            string id,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<NewTicketResult> CreateTicketAsync(
            string title,
            string? type,
            string descriptionHtml,
            IReadOnlyList<string>? initialLabelNames,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetParentAsync(
            string childUuid,
            string parentUuid,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> QueryAsync(
            TicketQuery query,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionLifecycleAsync(
            string id,
            LifecycleTransition transition,
            string? reason,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid,
            IReadOnlyList<ChildTicketSpec> children,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
