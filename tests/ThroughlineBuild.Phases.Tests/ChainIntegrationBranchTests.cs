using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class ChainIntegrationBranchTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task SweepChainWorktrees_RemovesTicketAndChainWorktrees_NotUnrelatedOrMain()
    {
        var git = new FakeGit();
        git.SetWorktrees(
            ("/wt/main", "main"),
            ("/wt/ticket-1", "ticket/tlb-1"),
            ("/wt/chain-9", "chain/tlb-9"),
            ("/wt/feature-x", "feature/x"));
        var collaborators = BuildCollaborators(git, CreateTempDirectory());

        await collaborators.IntegrationBranch.SweepChainWorktreesAsync(
            "TLB-1", collaborators.EventEmitter, CancellationToken.None);

        Assert.Contains("/wt/ticket-1", git.RemovedWorktrees);
        Assert.Contains("/wt/chain-9", git.RemovedWorktrees);
        Assert.DoesNotContain("/wt/feature-x", git.RemovedWorktrees);
        Assert.DoesNotContain("/wt/main", git.RemovedWorktrees);
    }

    [Fact]
    public async Task SweepChainWorktrees_EmitsAdvisoryEvent_WhenDecruftHalts_DoesNotThrow()
    {
        // Drive the real WorktreeDecrufter past both failed git removals and the absent-path
        // filesystem fallback into git worktree prune in a non-repository working directory.
        var workingDirectory = CreateTempDirectory();
        var git = new FakeGit { RemoveWorktreeFails = true };
        git.SetWorktrees(("/wt/ticket-halt", "ticket/tlb-1"));
        var collaborators = BuildCollaborators(git, workingDirectory);

        await collaborators.IntegrationBranch.SweepChainWorktreesAsync(
            "TLB-1", collaborators.EventEmitter, CancellationToken.None);

        var sweepEvent = Assert.Single(collaborators.Events.Events, e =>
            e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var kind)
            && (kind as string) == "worktree_sweep_incomplete");
        Assert.Equal(Phase.Chain, sweepEvent.Phase);
        Assert.Equal("TLB-1", sweepEvent.TicketId);
    }

    [Fact]
    public async Task SweepChainWorktrees_NoAdvisoryEvent_WhenAllRemovesSucceed()
    {
        var git = new FakeGit();
        git.SetWorktrees(("/wt/ticket-1", "ticket/tlb-1"));
        var collaborators = BuildCollaborators(git, CreateTempDirectory());

        await collaborators.IntegrationBranch.SweepChainWorktreesAsync(
            "TLB-1", collaborators.EventEmitter, CancellationToken.None);

        Assert.DoesNotContain(collaborators.Events.Events, e =>
            e.Kind == EventKind.GateFailure
            && e.Data.TryGetValue("kind", out var kind)
            && (kind as string) == "worktree_sweep_incomplete");
    }

    [Fact]
    public async Task SweepChainWorktrees_NeverThrows_WhenListWorktreesThrows()
    {
        var git = new FakeGit { ListWorktreesThrows = true };
        var collaborators = BuildCollaborators(git, CreateTempDirectory());

        var exception = await Record.ExceptionAsync(() =>
            collaborators.IntegrationBranch.SweepChainWorktreesAsync(
                "TLB-1", collaborators.EventEmitter, CancellationToken.None));

        Assert.Null(exception);
        Assert.Empty(git.RemovedWorktrees);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("origin", false)]
    public async Task LandRoot_DoesNotPush_WhenPushIsNotFullyConfigured(
        string? remote,
        bool pushEnabled)
    {
        var workingDirectory = CreateTempDirectory();
        var git = new FakeGit();
        var collaborators = BuildCollaborators(git, workingDirectory);
        var integrationBranch = new ChainIntegrationBranch(
            git, workingDirectory, remote, pushEnabled);

        var failure = await integrationBranch.LandRootIntegrationBranchAsync(
            "TLB-1",
            "chain/tlb-1",
            "/wt/chain-1",
            "main",
            () => collaborators.EventEmitter,
            CancellationToken.None);

        Assert.Null(failure);
        Assert.Empty(git.Pushes);
        Assert.Contains(("main", "/wt/chain-1"), git.Rebases);
        Assert.Contains(("chain/tlb-1", workingDirectory), git.FastForwardMerges);
    }

    [Fact]
    public async Task NestedChain_AccumulatesOntoParentIntegrationTarget()
    {
        var git = new FakeGit();
        var collaborators = BuildCollaborators(git, CreateTempDirectory());

        var failure = await collaborators.IntegrationBranch.RebaseThenFastForwardAsync(
            "TLB-2",
            "chain/tlb-2",
            "/wt/child-chain",
            "chain/tlb-1",
            "/wt/parent-chain",
            "chain_accumulate",
            () => collaborators.EventEmitter,
            CancellationToken.None);

        Assert.Null(failure);
        Assert.Contains(("chain/tlb-1", "/wt/child-chain"), git.Rebases);
        Assert.Contains(("chain/tlb-2", "/wt/parent-chain"), git.FastForwardMerges);
        Assert.Empty(git.Pushes);
    }

    [Fact]
    public void Naming_RemainsChainTicketSlug()
    {
        var ticket = MakeTicket();

        Assert.Equal("chain/tlb-574", ChainIntegrationBranch.BranchName(ticket));
        Assert.Equal("chain/tlb-574", ChainIntegrationBranch.BranchNameFromId("TLB-574"));
    }

    [Fact]
    public async Task RealGit_CreateRefreshRebaseFastForwardAndSweep()
    {
        var repositoryDirectory = CreateTempGitRepository();
        try
        {
            var git = new ProcessGitClient(repositoryDirectory);
            var integration = new ChainIntegrationBranch(git, repositoryDirectory, null, false);
            var ticket = MakeTicket();
            var branch = ChainIntegrationBranch.BranchName(ticket);
            var worktreePath = Path.Combine(repositoryDirectory, ".worktrees", "tlb-574");
            var ticketing = new StubTicketing();
            var events = new RecordingEventSink();
            Func<ChainEventEmitter> eventEmitterFactory =
                () => new ChainEventEmitter(events, ticketing, "integration-test");

            var create = await integration.EnsureIntegrationWorktreeAsync(
                branch, "main", worktreePath, CancellationToken.None);

            Assert.True(create.Success, create.FailureReason);
            Assert.True(Directory.Exists(worktreePath));

            File.WriteAllText(Path.Combine(worktreePath, "chain.txt"), "chain work\n");
            RunGit(worktreePath, "add", "chain.txt");
            RunGit(worktreePath, "commit", "-m", "TLB-574: chain work");
            var preRefreshBranchSha = RunGitOut(worktreePath, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repositoryDirectory, "main.txt"), "main moved\n");
            RunGit(repositoryDirectory, "add", "main.txt");
            RunGit(repositoryDirectory, "commit", "-m", "test: advance main");
            var advancedMainSha = RunGitOut(repositoryDirectory, "rev-parse", "HEAD");

            var refreshFailure = await integration.RefreshIntegrationBranchAsync(
                ticket.Id, branch, worktreePath, "main", eventEmitterFactory, CancellationToken.None);

            Assert.Null(refreshFailure);
            var refreshedBranchSha = RunGitOut(worktreePath, "rev-parse", "HEAD");
            Assert.NotEqual(preRefreshBranchSha, refreshedBranchSha);
            Assert.Equal(
                advancedMainSha,
                RunGitOut(repositoryDirectory, "merge-base", advancedMainSha, refreshedBranchSha));

            var landFailure = await integration.RebaseThenFastForwardAsync(
                ticket.Id, branch, worktreePath, "main", repositoryDirectory,
                "chain_landing", eventEmitterFactory, CancellationToken.None);

            Assert.Null(landFailure);
            Assert.Equal(refreshedBranchSha, RunGitOut(repositoryDirectory, "rev-parse", "HEAD"));

            await integration.SweepChainWorktreesAsync(
                ticket.Id, eventEmitterFactory(), CancellationToken.None);

            Assert.False(Directory.Exists(worktreePath));
            Assert.Contains(branch, RunGitOut(repositoryDirectory, "branch", "--list", branch));
        }
        finally
        {
            TryDeleteTree(repositoryDirectory);
        }
    }

    private static (
        ChainIntegrationBranch IntegrationBranch,
        ChainEventEmitter EventEmitter,
        RecordingEventSink Events)
        BuildCollaborators(FakeGit git, string workingDirectory)
    {
        var events = new RecordingEventSink();
        var ticketing = new StubTicketing();
        return (
            new ChainIntegrationBranch(git, workingDirectory, null, false),
            new ChainEventEmitter(events, ticketing, "session"),
            events);
    }

    private static Ticket MakeTicket() => new(
        Id: "TLB-574",
        Uuid: "ticket-uuid",
        Title: "Extract chain integration branch",
        Type: "feature",
        State: TicketState.Backlog,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<p>test</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlb-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateTempGitRepository()
    {
        var directory = CreateTempDirectory();
        RunGit(directory, "init", "-b", "main");
        RunGit(directory, "config", "user.email", "test@test.com");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "commit", "--allow-empty", "-m", "initial commit");
        return directory;
    }

    private static void RunGit(string workingDirectory, params string[] arguments) =>
        RunGitOut(workingDirectory, arguments);

    private static string RunGitOut(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed: {stderr}");
        return stdout.Trim();
    }

    private static void TryDeleteTree(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* Best-effort test cleanup. */ }
    }

    private sealed class RecordingEventSink : IEventSink
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task EmitAsync(WorkflowEvent workflowEvent, CancellationToken ct)
        {
            Events.Add(workflowEvent);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGit : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = new();

        public bool ListWorktreesThrows { get; init; }
        public bool RemoveWorktreeFails { get; init; }
        public List<string> RemovedWorktrees { get; } = new();
        public List<(string ontoRef, string worktreePath)> Rebases { get; } = new();
        public List<(string mergeRef, string worktreePath)> FastForwardMerges { get; } = new();
        public List<(string remote, string branch, string workingDirectory)> Pushes { get; } = new();

        public void SetWorktrees(params (string Path, string Branch)[] entries)
        {
            _worktrees.Clear();
            foreach (var entry in entries)
            {
                _worktrees.Add(new WorktreeInfo(
                    entry.Path, entry.Branch, HeadSha, false, false));
            }
        }

        public Task<string> RevParseAsync(
            string refspec,
            string workingDirectory,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
        {
            if (ListWorktreesThrows)
                throw new InvalidOperationException("git worktree list failed for test");
            return Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees.ToList());
        }

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path,
            bool force,
            CancellationToken ct)
        {
            RemovedWorktrees.Add(path);
            if (RemoveWorktreeFails)
                return Task.FromResult(new WorktreeRemoveResult(false, "remove failed for test"));
            _worktrees.RemoveAll(worktree =>
                string.Equals(worktree.Path, path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(
            string pattern,
            string baseBranch,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(HeadSha);

        public Task<GitDiff> DiffAsync(
            string fromRef,
            string toRef,
            string mainWorktreePath,
            bool includePatchContent,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> FetchAsync(
            string remote,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RebaseResult> RebaseAsync(
            string ontoRef,
            string featureWorktreePath,
            CancellationToken ct)
        {
            Rebases.Add((ontoRef, featureWorktreePath));
            return Task.FromResult(new RebaseResult(
                true, false, Array.Empty<string>(), null));
        }

        public Task<GitOpResult> RebaseAbortAsync(
            string featureWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<GitOpResult> FastForwardMergeAsync(
            string mergeRef,
            string mainWorktreePath,
            CancellationToken ct)
        {
            FastForwardMerges.Add((mergeRef, mainWorktreePath));
            return Task.FromResult(new GitOpResult(true, null));
        }

        public Task<GitOpResult> DeleteBranchAsync(
            string branch,
            bool force,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> RevListCountAsync(
            string range,
            string workingDirectory,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> LogOnelineAsync(
            string range,
            int limit,
            string workingDirectory,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CurrentBranchAsync(
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult("main");

        public Task<bool> RemoteExistsAsync(
            string remote,
            string workingDirectory,
            CancellationToken ct) =>
            Task.FromResult(true);

        public Task<GitOpResult> PushAsync(
            string remote,
            string branch,
            string workingDirectory,
            CancellationToken ct)
        {
            Pushes.Add((remote, branch, workingDirectory));
            return Task.FromResult(new GitOpResult(true, null));
        }
    }

    private sealed class StubTicketing : ITicketing
    {
        public BackendCapabilities Capabilities => new(false, false, true, false);

        public Task<Ticket> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Ticket>> GetBatchAsync(
            IEnumerable<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task TransitionAsync(string id, TicketState newState, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AppendDescriptionAsync(string id, string html, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateCommentAsync(string id, string html, CancellationToken ct) =>
            Task.FromResult("comment-id");

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
