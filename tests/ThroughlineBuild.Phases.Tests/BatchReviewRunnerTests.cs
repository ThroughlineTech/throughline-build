using System.Collections;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using ThroughlineBuild.Workers.Common;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class BatchReviewRunnerTests
{
    private const string FirstSha = "1111111111111111111111111111111111111111";
    private const string SecondSha = "2222222222222222222222222222222222222222";

    [Fact]
    public void MetadataParsers_MissingAndMalformed_DegradeWithoutThrowing()
    {
        var missing = new Dictionary<string, object>();
        var malformed = new Dictionary<string, object>
        {
            ["rationale"] = new ThrowingValue(),
            ["checks_failed"] = new ThrowingEnumerable()
        };

        Assert.Null(BatchReviewRunner.TryGetBatchReviewMetadataString(
            missing, "rationale"));
        Assert.Empty(BatchReviewRunner.ParseBatchReviewChecksFailed(missing));
        Assert.Null(BatchReviewRunner.TryGetBatchReviewMetadataString(
            malformed, "rationale"));
        Assert.Empty(BatchReviewRunner.ParseBatchReviewChecksFailed(malformed));
    }

    [Fact]
    public void ClassifyBatchRework_ExactlyOneIsLocalized_ZeroOrMultipleAreCrossTicket()
    {
        var tickets = new[]
        {
            MakeTicket("TLB-10"),
            MakeTicket("TLB-20"),
            MakeTicket("TLB-30")
        };

        Assert.Equal(
            BatchReviewRunner.BatchReworkRoute.Localized,
            BatchReviewRunner.ClassifyBatchRework(
                tickets, "TLB-20 needs a null check."));
        Assert.Equal(
            BatchReviewRunner.BatchReworkRoute.CrossTicket,
            BatchReviewRunner.ClassifyBatchRework(
                tickets, "The integration seam needs work."));
        Assert.Equal(
            BatchReviewRunner.BatchReworkRoute.CrossTicket,
            BatchReviewRunner.ClassifyBatchRework(
                tickets, "TLB-10 and TLB-30 share a broken contract."));
    }

    [Fact]
    public async Task PostBatchReviewCommentAsync_PreservesFixedHtmlFormat()
    {
        var ticketing = new FakeTicketing();
        var runner = BuildRunner(new QueueWorker(), new FakeGit(), ticketing);

        await runner.PostBatchReviewCommentAsync(
            "TLB-10",
            2,
            VerdictKind.Rework,
            "fix <unsafe> & retry",
            new[] { "unit", "lint" },
            CancellationToken.None);

        var comment = Assert.Single(ticketing.Comments);
        Assert.Equal("TLB-10", comment.TicketId);
        Assert.Equal(
            "<p>[batch_review (pass 2): Rework] checks_failed: unit, lint</p>" +
            "<p>fix &lt;unsafe&gt; &amp; retry</p>",
            comment.Html);
    }

    [Fact]
    public async Task RunCombinedBatchReviewAsync_MissingMetadata_DegradesToFail()
    {
        var ticketing = new FakeTicketing();
        var worker = new QueueWorker(WorkerResultWithMetadata(
            new Dictionary<string, object>()));
        var runner = BuildRunner(worker, new FakeGit(), ticketing);
        var tickets = new[] { MakeTicket("TLB-10") };

        var outcome = await runner.RunCombinedBatchReviewAsync(
            tickets,
            Confirmed(tickets),
            "ticket/tlb-10",
            "main",
            CreateTempDirectory(),
            "chain-session",
            CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(VerdictKind.Fail, outcome.FinalVerdict);
        Assert.Equal(1, worker.CallCount);
        Assert.Equal(
            "<p>[batch_review: Fail]</p><p></p>",
            Assert.Single(ticketing.Comments).Html);
    }

    [Fact]
    public async Task RunBatchReviewAndReworkAsync_CrossTicket_UsesExactlyTwoReworkRounds()
    {
        var tickets = new[] { MakeTicket("TLB-10"), MakeTicket("TLB-20") };
        var reworkMetadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale"] = "The integration seam needs work.",
            ["checks_failed"] = new[] { "integration" }
        };
        var worker = new QueueWorker(
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            CrossTicketWorkerResult(tickets),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            CrossTicketWorkerResult(tickets),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata));
        var git = new FakeGit { LogShas = new[] { SecondSha, FirstSha } };
        var ticketing = new FakeTicketing();
        var runner = BuildRunner(worker, git, ticketing);

        var passed = await runner.RunBatchReviewAndReworkAsync(
            tickets,
            Confirmed(tickets),
            "ticket/tlb-10",
            "main",
            CreateTempDirectory(),
            null,
            CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(8, worker.CallCount);
        Assert.Equal(
            4,
            ticketing.Transitions.Count(transition =>
                transition.State == TicketState.InProgress));
        Assert.Equal(
            4,
            ticketing.Transitions.Count(transition =>
                transition.State == TicketState.InReview));
    }

    [Fact]
    public async Task RunBatchReviewAndReworkAsync_Localized_DispatchesExactlyTwoRoundsWithNumberedFeedback()
    {
        var tickets = new[] { MakeTicket("TLB-10"), MakeTicket("TLB-20") };
        var reworkMetadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale"] = "TLB-10 needs a null check.",
            ["checks_failed"] = new[] { "unit" }
        };
        var batchWorker = new QueueWorker(
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata),
            WorkerResultWithMetadata(reworkMetadata));
        var implementWorker = new QueueWorker(
            ImplementWorkerResult(),
            ImplementWorkerResult());
        var git = new FakeGit();
        var ticketing = new FakeTicketing(tickets);
        var workingDirectory = CreateTempDirectory();
        var sharedWorktreePath = CreateTempDirectory();
        var feedbackRounds = new List<int>();
        var runner = BuildRunner(
            batchWorker,
            git,
            ticketing,
            workingDirectory,
            (options, phaseOptions) =>
            {
                feedbackRounds.Add(
                    phaseOptions.ReviewFeedback!.ReworkRoundNumber);
                return new ImplementPhase(
                    ticketing,
                    implementWorker,
                    new RecordingEventSink(),
                    options,
                    git,
                    phaseOptions: phaseOptions);
            });

        var passed = await runner.RunBatchReviewAndReworkAsync(
            tickets,
            Confirmed(tickets),
            "ticket/tlb-10",
            "main",
            sharedWorktreePath,
            null,
            CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(6, batchWorker.CallCount);
        Assert.Equal(2, implementWorker.CallCount);
        Assert.Equal(new[] { 1, 2 }, feedbackRounds);
    }

    private static BatchReviewRunner BuildRunner(
        IWorkerAgent worker,
        IGitClient git,
        ITicketing ticketing,
        string? workingDirectory = null,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase>?
            implementFactory = null)
    {
        var baseOptions = new BuildOptions(
            SessionId: "base",
            WorkerName: "claude-code",
            WorkerTimeout: TimeSpan.FromMinutes(1));
        var events = new RecordingEventSink();
        var session = 0;
        return new BatchReviewRunner(
            worker,
            ticketing,
            git,
            baseOptions,
            workingDirectory ?? CreateTempDirectory(),
            () => $"session-{++session}",
            sessionId => new ChainEventEmitter(events, ticketing, sessionId),
            implementFactory ?? ((_, _) =>
                throw new InvalidOperationException(
                    "localized implement factory not used")),
            new PhaseOptionsBuilder(baseOptions));
    }

    private static WorkerResult WorkerResultWithMetadata(
        IReadOnlyDictionary<string, object> metadata) =>
        new(
            Status.Ok,
            "review complete",
            Array.Empty<string>(),
            null,
            metadata);

    private static WorkerResult ImplementWorkerResult() =>
        new(
            Status.Ok,
            "implemented",
            new[] { "src/Foo.cs" },
            null,
            new Dictionary<string, object>
            {
                ["commit_sha"] = SecondSha,
                ["files_changed"] = new[] { "src/Foo.cs" }
            });

    private static WorkerResult CrossTicketWorkerResult(
        IReadOnlyList<Ticket> tickets) =>
        new(
            Status.Ok,
            "rework complete",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>(),
            Tickets: Confirmed(tickets));

    private static IReadOnlyList<BatchTicketResult> Confirmed(
        IReadOnlyList<Ticket> tickets) =>
        tickets.Select((ticket, index) => new BatchTicketResult(
            ticket.Id,
            index == 0 ? FirstSha : SecondSha,
            index,
            Array.Empty<string>(),
            $"SUMMARY_{index + 1}")).ToList();

    private static Ticket MakeTicket(string id) => new(
        Id: id,
        Uuid: $"uuid-{id}",
        Title: $"Ticket {id}",
        Type: "feature",
        State: TicketState.InReview,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>plan</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: "TLB-1");

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "tlb-batch-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingValue
    {
        public override string ToString() =>
            throw new InvalidOperationException("malformed metadata");
    }

    private sealed class ThrowingEnumerable : IEnumerable<object>
    {
        public IEnumerator<object> GetEnumerator() =>
            throw new InvalidOperationException("malformed metadata");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class QueueWorker(params WorkerResult[] results) : IWorkerAgent
    {
        private readonly Queue<WorkerResult> _results = new(results);

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
            return Task.FromResult(_results.Dequeue());
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
            Task.FromResult("0000000000000000000000000000000000000000");

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
            Task.FromResult(new GitDiff(
                fromRef, toRef, Array.Empty<DiffEntry>()));

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
        private readonly Dictionary<string, Ticket> _tickets;

        public FakeTicketing(IEnumerable<Ticket>? tickets = null)
        {
            _tickets = (tickets ?? Array.Empty<Ticket>())
                .ToDictionary(ticket => ticket.Id, StringComparer.Ordinal);
        }

        public List<(string TicketId, TicketState State)> Transitions { get; } = new();
        public List<(string TicketId, string Html)> Comments { get; } = new();
        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task TransitionAsync(
            string id,
            TicketState newState,
            CancellationToken ct)
        {
            Transitions.Add((id, newState));
            if (_tickets.TryGetValue(id, out var ticket))
                _tickets[id] = ticket with { State = newState };
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
            Task.FromResult(_tickets[id]);

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
            Task.FromResult<IReadOnlyList<TicketComment>>(
                Array.Empty<TicketComment>());

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
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

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
