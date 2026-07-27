using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public sealed class ChainPreflightTests
{
    private const string WorkingDirectory = "/repo";
    private const string TicketBranch = "ticket/tlb-570";

    [Fact]
    public async Task CheckAsync_WrongBranch_RefusesBeforeHygieneAndTrackedChecks()
    {
        var git = new FakeGit(
            currentBranch: "ticket/other",
            conflictedPaths: new[] { "conflict.txt" },
            stashEntries: new[] { "stash@{0}: WIP on main: work" },
            trackedChanges: new[] { "dirty.txt" });
        var preflight = new ChainPreflight(git, WorkingDirectory, "main");

        var refusal = await preflight.CheckAsync(TicketBranch, CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Equal(ChainOutcome.RefusedWrongBranch, refusal.Outcome);
        Assert.Null(refusal.DirtyTreeCause);
        Assert.Contains("must be on 'main'", refusal.Message);
        Assert.Equal(new[] { "current_branch" }, git.Calls);
        Assert.Equal(
            new[] { "actual", "expected", "kind", "worktree" },
            refusal.EventData.Keys.OrderBy(key => key));
        Assert.Equal("chain_preflight_wrong_branch", refusal.EventData["kind"]);
        Assert.Equal("main", refusal.EventData["expected"]);
        Assert.Equal("ticket/other", refusal.EventData["actual"]);
        Assert.Equal(WorkingDirectory, refusal.EventData["worktree"]);
    }

    [Fact]
    public async Task CheckAsync_HygieneFailure_RefusesBeforeTrackedCheck()
    {
        var git = new FakeGit(
            currentBranch: "main",
            conflictedPaths: new[] { "src/conflict.cs" },
            stashEntries: Array.Empty<string>(),
            trackedChanges: new[] { "dirty.txt" });
        var preflight = new ChainPreflight(git, WorkingDirectory, "main");

        var refusal = await preflight.CheckAsync(TicketBranch, CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Equal(ChainOutcome.RefusedDirtyTree, refusal.Outcome);
        Assert.Equal(DirtyTreeCause.Hygiene, refusal.DirtyTreeCause);
        Assert.Equal(
            new[] { "current_branch", "conflicts", "stashes" },
            git.Calls);
        Assert.Equal(
            new[] { "detail", "kind" },
            refusal.EventData.Keys.OrderBy(key => key));
        Assert.Equal("hygiene_gate_preflight", refusal.EventData["kind"]);
        Assert.Equal(refusal.Message, refusal.EventData["detail"]);
        Assert.Contains("unmerged/conflicted paths: src/conflict.cs", refusal.Message);
    }

    [Fact]
    public async Task CheckAsync_TrackedChanges_RefusesWithBoundedPathSample()
    {
        var dirtyPaths = Enumerable.Range(1, 27)
            .Select(index => $"src/dirty-{index}.cs")
            .ToArray();
        var git = new FakeGit(
            currentBranch: "main",
            conflictedPaths: Array.Empty<string>(),
            stashEntries: Array.Empty<string>(),
            trackedChanges: dirtyPaths);
        var preflight = new ChainPreflight(git, WorkingDirectory, "main");

        var refusal = await preflight.CheckAsync(TicketBranch, CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Equal(ChainOutcome.RefusedDirtyTree, refusal.Outcome);
        Assert.Equal(DirtyTreeCause.TrackedChanges, refusal.DirtyTreeCause);
        Assert.Equal(
            new[] { "current_branch", "conflicts", "stashes", "tracked" },
            git.Calls);
        Assert.Equal(
            new[] { "dirty_count", "dirty_paths", "kind", "worktree" },
            refusal.EventData.Keys.OrderBy(key => key));
        Assert.Equal("chain_preflight_dirty", refusal.EventData["kind"]);
        Assert.Equal(27, refusal.EventData["dirty_count"]);
        var sampledPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            refusal.EventData["dirty_paths"]);
        Assert.Equal(dirtyPaths.Take(25), sampledPaths);
        Assert.Equal(WorkingDirectory, refusal.EventData["worktree"]);
        Assert.Contains("src/dirty-25.cs (+2 more)", refusal.Message);
        Assert.DoesNotContain("src/dirty-26.cs", refusal.Message);
    }

    [Fact]
    public async Task CheckAsync_CleanTree_PassesAfterAllChecksInOrder()
    {
        var git = new FakeGit(
            currentBranch: "main",
            conflictedPaths: Array.Empty<string>(),
            stashEntries: Array.Empty<string>(),
            trackedChanges: Array.Empty<string>());
        var preflight = new ChainPreflight(git, WorkingDirectory, "main");

        var refusal = await preflight.CheckAsync(TicketBranch, CancellationToken.None);

        Assert.Null(refusal);
        Assert.Equal(
            new[] { "current_branch", "conflicts", "stashes", "tracked" },
            git.Calls);
    }

    private sealed class FakeGit : IGitClient
    {
        private readonly string _currentBranch;
        private readonly IReadOnlyList<string> _conflictedPaths;
        private readonly IReadOnlyList<string> _stashEntries;
        private readonly IReadOnlyList<string> _trackedChanges;

        public FakeGit(
            string currentBranch,
            IReadOnlyList<string> conflictedPaths,
            IReadOnlyList<string> stashEntries,
            IReadOnlyList<string> trackedChanges)
        {
            _currentBranch = currentBranch;
            _conflictedPaths = conflictedPaths;
            _stashEntries = stashEntries;
            _trackedChanges = trackedChanges;
        }

        public List<string> Calls { get; } = new();

        public Task<string> CurrentBranchAsync(string workingDirectory, CancellationToken ct)
        {
            Calls.Add("current_branch");
            return Task.FromResult(_currentBranch);
        }

        public Task<IReadOnlyList<string>> GetConflictedPathsAsync(
            string workingDirectory,
            CancellationToken ct)
        {
            Calls.Add("conflicts");
            return Task.FromResult(_conflictedPaths);
        }

        public Task<IReadOnlyList<string>> ListStashEntriesAsync(
            string workingDirectory,
            CancellationToken ct)
        {
            Calls.Add("stashes");
            return Task.FromResult(_stashEntries);
        }

        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(
            string workingDirectory,
            CancellationToken ct)
        {
            Calls.Add("tracked");
            return Task.FromResult(_trackedChanges);
        }

        public Task<string> RevParseAsync(
            string refspec,
            string workingDirectory,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(
            string path,
            bool force,
            CancellationToken ct) =>
            throw new NotSupportedException();

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
            throw new NotSupportedException();

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
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> RebaseAbortAsync(
            string featureWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitOpResult> FastForwardMergeAsync(
            string mergeRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            throw new NotSupportedException();

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
    }
}
