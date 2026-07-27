using System.Diagnostics;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public sealed class ImplementReviewLoopTests
{
    private const string TicketId = "TLB-577";
    private const string MainSha =
        "1111111111111111111111111111111111111111";
    private const string CommitSha =
        "2222222222222222222222222222222222222222";

    [Fact]
    public async Task RunImplementReviewLoopAsync_RecheckRetriesTwiceInSameRound()
    {
        var workingDirectory = CreateTempDirectory();
        var sharedWorktree = CreateTempDirectory();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var git = new FakeGit(sharedWorktree);
        var events = new RecordingEventSink();
        var implementWorker = new CountingWorker();
        var verifier = new QueueVerifier(new Verdict(
            VerdictKind.Rework,
            "lint fails",
            new[] { "lint" }));
        var failedCheck = FailedCheck();
        var recheck = new ScriptedChecksRunner(
            failedCheck,
            failedCheck,
            failedCheck);
        var feedbackRounds = new List<int?>();
        var loop = BuildLoop(
            ticketing,
            git,
            events,
            workingDirectory,
            (options, phaseOptions) =>
            {
                feedbackRounds.Add(
                    phaseOptions.ReviewFeedback?.ReworkRoundNumber);
                return new ImplementPhase(
                    ticketing,
                    implementWorker,
                    events,
                    options,
                    git,
                    phaseOptions: phaseOptions);
            },
            (options, _) => new ReviewPhase(
                ticketing,
                new CountingWorker(),
                events,
                options,
                MakeReviewOptions(),
                git,
                verifierOverride: verifier,
                checksRunner: new PreComputedChecksRunner(
                    new[] { failedCheck })),
            gateFactory: null,
            reworkSpecs: new[] { LintSpec() },
            reworkRunner: recheck);
        var steps = new List<ChainStep>();

        var result = await loop.RunImplementReviewLoopAsync(
            new ChainPhaseOptions(
                TicketId,
                Debug: false,
                SharedWorktreePath: sharedWorktree),
            steps,
            "chain-session",
            0,
            null,
            Stopwatch.StartNew(),
            CancellationToken.None);

        Assert.NotNull(result.abort);
        Assert.Equal(
            ChainOutcome.ReworkCapExceeded,
            result.abort.Outcome);
        Assert.Equal(4, implementWorker.CallCount);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(new int?[] { null, 1, 1, 1 }, feedbackRounds);
        Assert.Equal(new[] { "lint", "lint", "lint" }, recheck.Names);
        Assert.Equal(
            new[] { 0, 1, 1, 1 },
            steps.Where(step => step.PhaseName == "implement")
                .Select(step => step.ReworkRoundNumber));
        Assert.Contains("lint --strict", result.abort.FinalRationale);
    }

    [Fact]
    public async Task RunImplementReviewLoopAsync_ZeroGatingChecksCannotPass()
    {
        var context = MakeGateContext();
        var reviewDispatches = 0;
        var loop = BuildLoop(
            context.Ticketing,
            context.Git,
            context.Events,
            context.WorkingDirectory,
            (options, phaseOptions) => new ImplementPhase(
                context.Ticketing,
                context.Worker,
                context.Events,
                options,
                context.Git,
                phaseOptions: phaseOptions),
            (options, gate) =>
            {
                reviewDispatches++;
                return MakeReviewPhase(
                    context,
                    options,
                    new Verdict(
                        VerdictKind.Pass,
                        "unexpected",
                        Array.Empty<string>()));
            },
            options => new GatePhase(
                context.Ticketing,
                context.Events,
                options,
                new GateOptions(Array.Empty<CheckSpec>()),
                context.Git,
                new PreComputedChecksRunner(
                    Array.Empty<CheckResult>())));

        var result = await loop.RunImplementReviewLoopAsync(
            context.Options,
            new List<ChainStep>(),
            "chain-session",
            0,
            null,
            Stopwatch.StartNew(),
            CancellationToken.None);

        Assert.NotNull(result.abort);
        Assert.Equal(ChainOutcome.GateVacuous, result.abort.Outcome);
        Assert.Contains(
            "no gating checks",
            result.abort.FinalRationale);
        Assert.Equal(0, reviewDispatches);
    }

    [Fact]
    public async Task RunImplementReviewLoopAsync_FailingGatingCheckStillFails()
    {
        var context = MakeGateContext();
        var reviewDispatches = 0;
        var failedCheck = FailedCheck();
        var loop = BuildLoop(
            context.Ticketing,
            context.Git,
            context.Events,
            context.WorkingDirectory,
            (options, phaseOptions) => new ImplementPhase(
                context.Ticketing,
                context.Worker,
                context.Events,
                options,
                context.Git,
                phaseOptions: phaseOptions),
            (options, gate) =>
            {
                reviewDispatches++;
                return MakeReviewPhase(
                    context,
                    options,
                    new Verdict(
                        VerdictKind.Pass,
                        "unexpected",
                        Array.Empty<string>()));
            },
            options => new GatePhase(
                context.Ticketing,
                context.Events,
                options,
                new GateOptions(new[] { LintSpec() }),
                context.Git,
                new PreComputedChecksRunner(
                    new[] { failedCheck })));
        var steps = new List<ChainStep>();

        var result = await loop.RunImplementReviewLoopAsync(
            context.Options,
            steps,
            "chain-session",
            2,
            null,
            Stopwatch.StartNew(),
            CancellationToken.None);

        Assert.NotNull(result.abort);
        Assert.Equal(
            ChainOutcome.ReworkCapExceeded,
            result.abort.Outcome);
        Assert.Equal(0, reviewDispatches);
        var gateStep = Assert.Single(
            steps,
            step => step.PhaseName == "gate");
        Assert.Equal(Status.Failed, gateStep.Status);
        Assert.Contains("lint failed", gateStep.FailureReason);
    }

    private static ImplementReviewLoop BuildLoop(
        FakeTicketing ticketing,
        FakeGit git,
        RecordingEventSink events,
        string workingDirectory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase>
            implementFactory,
        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory,
        Func<BuildOptions, GatePhase>? gateFactory,
        IReadOnlyList<CheckSpec>? reworkSpecs = null,
        AutomatedChecksRunner? reworkRunner = null)
    {
        var baseOptions = MakeBuildOptions();
        var phaseOptionsBuilder = new PhaseOptionsBuilder(baseOptions);
        var session = 0;
        return new ImplementReviewLoop(
            ticketing,
            implementFactory,
            reviewFactory,
            gateFactory,
            ratifierFactory: null,
            () => $"session-{++session}",
            id => new ChainEventEmitter(events, ticketing, id),
            phaseOptionsBuilder,
            baseOptions,
            workingDirectory,
            git,
            reworkSpecs,
            reworkRunner);
    }

    private static GateContext MakeGateContext()
    {
        var workingDirectory = CreateTempDirectory();
        var sharedWorktree = CreateTempDirectory();
        var ticketing = new FakeTicketing(MakeTicket(TicketState.Ready));
        var git = new FakeGit(sharedWorktree);
        var events = new RecordingEventSink();
        return new GateContext(
            ticketing,
            git,
            events,
            new CountingWorker(),
            workingDirectory,
            new ChainPhaseOptions(
                TicketId,
                Debug: false,
                SharedWorktreePath: sharedWorktree));
    }

    private static ReviewPhase MakeReviewPhase(
        GateContext context,
        BuildOptions options,
        Verdict verdict) =>
        new(
            context.Ticketing,
            new CountingWorker(),
            context.Events,
            options,
            MakeReviewOptions(),
            context.Git,
            verifierOverride: new QueueVerifier(verdict));

    private static BuildOptions MakeBuildOptions() =>
        new(
            SessionId: "base",
            WorkerName: "claude-code",
            WorkerTimeout: TimeSpan.FromMinutes(1));

    private static ReviewOptions MakeReviewOptions() =>
        new(
            Array.Empty<CheckSpec>(),
            new WorkerOptions(TimeSpan.FromMinutes(1), null));

    private static CheckSpec LintSpec() =>
        new(
            "lint",
            "lint",
            new[] { "--strict" },
            TimeSpan.FromMinutes(1));

    private static CheckResult FailedCheck() =>
        new(
            "lint",
            Passed: false,
            ExitCode: 1,
            StdoutTail: "",
            StderrTail: "sorting failed",
            Elapsed: TimeSpan.FromMilliseconds(10),
            Role: CheckRole.Gating,
            CommandLine: "lint --strict");

    private static Ticket MakeTicket(TicketState state) =>
        new(
            TicketId,
            "uuid-tlb-577",
            "Extract implement review loop",
            "feature",
            state,
            Size.S,
            Risk.Low,
            "<p>plan</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "tlb-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record GateContext(
        FakeTicketing Ticketing,
        FakeGit Git,
        RecordingEventSink Events,
        CountingWorker Worker,
        string WorkingDirectory,
        ChainPhaseOptions Options);

    private sealed class ScriptedChecksRunner(
        params CheckResult[] results) : AutomatedChecksRunner
    {
        private readonly Queue<CheckResult> _results = new(results);
        public List<string> Names { get; } = new();

        public override Task<CheckResult> RunNamedAsync(
            string checkName,
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct)
        {
            Names.Add(checkName);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class QueueVerifier(
        params Verdict[] verdicts) : IVerifier
    {
        private readonly Queue<Verdict> _verdicts = new(verdicts);
        public int CallCount { get; private set; }

        public Task<Verdict> VerifyAsync(
            Brief brief,
            GitDiff diff,
            WorkerResult workerResult,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_verdicts.Dequeue());
        }
    }

    private sealed class CountingWorker : IWorkerAgent
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
            return Task.FromResult(new WorkerResult(
                Status.Ok,
                "implemented",
                Array.Empty<string>(),
                null,
                new Dictionary<string, object>
                {
                    ["commit_sha"] = CommitSha,
                    ["files_changed"] = Array.Empty<string>()
                }));
        }
    }

    private sealed class RecordingEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(
            WorkflowEvent workflowEvent,
            CancellationToken ct)
        {
            Events.Add(workflowEvent);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeGit(string sharedWorktree) : IGitClient
    {
        public Task<string> RevParseAsync(
            string refspec,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(MainSha);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(
                new[]
                {
                    new WorktreeInfo(
                        sharedWorktree,
                        "ticket/tlb-577-extract-implement-review-loop",
                        CommitSha,
                        false,
                        false)
                });

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
            Task.FromResult(
                new WorktreeCreateResult(true, null, worktreePath));

        public Task<GitOpResult> CreateBranchAsync(
            string branch,
            string fromRef,
            string worktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<string> HeadShaAsync(
            string worktreePath,
            CancellationToken ct) =>
            Task.FromResult(CommitSha);

        public Task<GitDiff> DiffAsync(
            string fromRef,
            string toRef,
            string mainWorktreePath,
            bool includePatchContent,
            CancellationToken ct) =>
            Task.FromResult(new GitDiff(
                fromRef,
                toRef,
                Array.Empty<DiffEntry>()));

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
                true,
                false,
                Array.Empty<string>(),
                null));

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
    }

    private sealed class FakeTicketing(Ticket ticket) : ITicketing
    {
        private Ticket _ticket = ticket;
        private readonly List<TicketComment> _comments = new();

        public BackendCapabilities Capabilities =>
            new(false, false, true, false);

        public Task<Ticket> GetAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult(_ticket);

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(new[] { _ticket });

        public Task TransitionAsync(
            string id,
            TicketState newState,
            CancellationToken ct)
        {
            _ticket = _ticket with { State = newState };
            return Task.CompletedTask;
        }

        public Task AppendDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string> CreateCommentAsync(
            string id,
            string html,
            CancellationToken ct)
        {
            _comments.Add(new TicketComment(
                Guid.NewGuid().ToString("N"),
                html,
                DateTimeOffset.UtcNow));
            return Task.FromResult("comment-id");
        }

        public Task ApplyLabelsAsync(
            string id,
            IEnumerable<string> labels,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Relation>> GetRelationsAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Relation>>(
                Array.Empty<Relation>());

        public Task AddRelationAsync(
            string blockedId,
            string blockerId,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<RollupResult> RollupParentAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult(new RollupResult(false, null, null));

        public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TicketComment>>(
                _comments.ToList());

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
            Task.CompletedTask;

        public Task<IReadOnlyList<Ticket>> QueryAsync(
            TicketQuery query,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Array.Empty<Ticket>());

        public Task TransitionLifecycleAsync(
            string id,
            LifecycleTransition transition,
            string? reason,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(
            string id,
            string html,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<CreateChildTicketsResult> CreateChildTicketsAsync(
            string parentUuid,
            IReadOnlyList<ChildTicketSpec> children,
            CancellationToken ct) =>
            Task.FromResult(new CreateChildTicketsResult(
                Array.Empty<CreatedChild>(),
                Array.Empty<string>()));
    }
}
