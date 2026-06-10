using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class ChainWorktreeSweeperTests
{
    private const string Target = "main";
    private const string Main = "/repo";

    // -- merged ticket/chain worktrees: worktree removed AND branch deleted ------------------

    [Fact]
    public async Task MergedWorktreeBranch_RemovesWorktree_AndDeletesBranch()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-3", "ticket/3")
            .WithMerged("ticket/3");
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: false, CancellationToken.None);

        Assert.Contains("/repo/.worktrees/ticket-3", decrufter.Removed);
        Assert.Contains("ticket/3", result.BranchesDeleted);
        Assert.Contains("ticket/3", git.DeletedBranches);
        Assert.Empty(result.BranchesKeptUnmerged);
        Assert.True(result.FullyClean);
    }

    // -- the experiment-4 shape: nested chain/N integration + ticket/N leaves, all merged ----

    [Fact]
    public async Task NestedChainAndTicketBranches_AllMerged_AllSwept()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-1", "chain/1")
            .WithWorktree("/repo/.worktrees/ticket-2", "chain/2")
            .WithWorktree("/repo/.worktrees/ticket-7", "chain/7")
            .WithWorktree("/repo/.worktrees/ticket-3", "ticket/3")
            .WithWorktree("/repo/.worktrees/ticket-8", "ticket/8")
            .WithMerged("chain/1", "chain/2", "chain/7", "ticket/3", "ticket/8");
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: false, CancellationToken.None);

        Assert.Equal(5, result.WorktreesRemoved.Count);
        Assert.Equal(
            new[] { "chain/1", "chain/2", "chain/7", "ticket/3", "ticket/8" },
            result.BranchesDeleted.OrderBy(b => b).ToArray());
        Assert.True(result.FullyClean);
    }

    // -- unmerged branch is never destroyed -------------------------------------------------

    [Fact]
    public async Task UnmergedWorktreeBranch_NoForce_KeepsWorktreeAndBranch()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-9", "ticket/9"); // not merged
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: false, CancellationToken.None);

        Assert.Empty(decrufter.Removed);             // worktree preserved
        Assert.Empty(git.DeletedBranches);           // branch never deleted
        Assert.Empty(result.BranchesDeleted);
        Assert.Contains("ticket/9", result.BranchesKeptUnmerged);
    }

    [Fact]
    public async Task UnmergedWorktreeBranch_Force_RemovesWorktree_ButKeepsBranch()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-9", "ticket/9"); // not merged
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: true, CancellationToken.None);

        Assert.Contains("/repo/.worktrees/ticket-9", decrufter.Removed); // forced worktree removal
        Assert.Empty(git.DeletedBranches);                              // commits never lost
        Assert.Contains("ticket/9", result.BranchesKeptUnmerged);
    }

    // -- orphan branches (no worktree) ------------------------------------------------------

    [Fact]
    public async Task OrphanMergedBranch_Deleted_OrphanUnmergedBranch_Kept()
    {
        var git = new FakeGit()
            .WithLocalBranch("ticket/4")   // merged orphan
            .WithLocalBranch("chain/5")    // unmerged orphan
            .WithMerged("ticket/4");
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: false, CancellationToken.None);

        Assert.Contains("ticket/4", result.BranchesDeleted);
        Assert.Contains("chain/5", result.BranchesKeptUnmerged);
        Assert.Empty(decrufter.Removed); // no worktrees to remove
    }

    // -- safety: non-chain worktrees and the main checkout are never touched -----------------

    [Fact]
    public async Task NonChainWorktrees_Untouched()
    {
        var git = new FakeGit()
            .WithWorktree("/repo", "main")
            .WithWorktree("/repo/.worktrees/feature-x", "feature/x")
            .WithMerged("feature/x"); // even if merged, not a chain branch
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: true, CancellationToken.None);

        Assert.Empty(decrufter.Removed);
        Assert.Empty(result.BranchesDeleted);
        Assert.Empty(result.WorktreesRemoved);
    }

    // -- a stuck worktree is reported and its branch is left checked out (not deleted) -------

    [Fact]
    public async Task HaltedDecruft_ReportedAndBranchNotDeleted()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-3", "ticket/3")
            .WithMerged("ticket/3");
        var decrufter = new FakeDecrufter().HaltOn("/repo/.worktrees/ticket-3");

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: true, CancellationToken.None);

        Assert.Single(result.WorktreesHalted);
        Assert.Empty(result.WorktreesRemoved);
        Assert.Empty(git.DeletedBranches);          // can't delete a branch a stuck worktree holds
        Assert.False(result.FullyClean);
    }

    // -- a branch delete that fails is surfaced, not silently dropped ------------------------

    [Fact]
    public async Task BranchDeleteFailure_ReportedAsKept()
    {
        var git = new FakeGit()
            .WithWorktree("/repo/.worktrees/ticket-3", "ticket/3")
            .WithMerged("ticket/3")
            .WithDeleteFailure("ticket/3");
        var decrufter = new FakeDecrufter();

        var result = await new ChainWorktreeSweeper(git, decrufter)
            .SweepAsync(Main, Target, force: false, CancellationToken.None);

        Assert.Contains("/repo/.worktrees/ticket-3", result.WorktreesRemoved);
        Assert.Empty(result.BranchesDeleted);
        Assert.Contains("ticket/3", result.BranchesKeptUnmerged);
    }

    // ---------------------------------------------------------------------------------------

    private sealed class FakeDecrufter : WorktreeDecrufter
    {
        private readonly HashSet<string> _halt = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; } = new();

        public FakeDecrufter() : base(new FakeGit()) { }

        public FakeDecrufter HaltOn(string path) { _halt.Add(path); return this; }

        public override Task<DecruftResult> DecruftAsync(string worktreePath, string mainWorktreePath, CancellationToken ct)
        {
            var outcomes = new Dictionary<DecruftStep, DecruftStepOutcome>();
            if (_halt.Contains(worktreePath))
                return Task.FromResult(new DecruftResult(DecruftStep.GitWorktreePrune, outcomes));
            Removed.Add(worktreePath);
            return Task.FromResult(new DecruftResult(null, outcomes));
        }
    }

    private sealed class FakeGit : IGitClient
    {
        private readonly List<WorktreeInfo> _worktrees = new();
        private readonly List<string> _localBranches = new();
        private readonly HashSet<string> _merged = new(StringComparer.Ordinal);
        private readonly HashSet<string> _deleteFail = new(StringComparer.Ordinal);
        public List<string> DeletedBranches { get; } = new();

        public FakeGit WithWorktree(string path, string branch)
        {
            _worktrees.Add(new WorktreeInfo(path, branch, "deadbeef", false, false));
            return this;
        }
        public FakeGit WithLocalBranch(string branch) { _localBranches.Add(branch); return this; }
        public FakeGit WithMerged(params string[] branches) { foreach (var b in branches) _merged.Add(b); return this; }
        public FakeGit WithDeleteFailure(string branch) { _deleteFail.Add(branch); return this; }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(_worktrees);

        public Task<bool> IsAncestorAsync(string ancestor, string descendant, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_merged.Contains(ancestor));

        public Task<IReadOnlyList<string>> ListLocalBranchesAsync(string pattern, string workingDirectory, CancellationToken ct)
        {
            var prefix = pattern.TrimEnd('*');
            // Include both worktree-backed and orphan branches matching the prefix, mirroring
            // `git branch --list <prefix>*`. The sweeper dedups via its processed set.
            var all = _worktrees.Select(w => w.Branch).Concat(_localBranches)
                .Where(b => b.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct()
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(all);
        }

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct)
        {
            if (_deleteFail.Contains(branch))
                return Task.FromResult(new GitOpResult(false, "delete failed"));
            DeletedBranches.Add(branch);
            return Task.FromResult(new GitOpResult(true, null));
        }

        // -- unused abstract members (the sweeper never calls these) ------------------------
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(string.Empty);
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(string.Empty);
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
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
