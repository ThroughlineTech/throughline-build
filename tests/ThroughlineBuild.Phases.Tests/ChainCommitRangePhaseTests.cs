using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Pins the chain-start SHA capture and ChainCommitRange derivation contract (Brief 08, Plan C).
///
/// Tests are hermetic: no real repository, no real git process.
/// They exercise ChainCommitRangeHelper directly with controlled git fakes to confirm
/// the range, count, and touched-files invariants described in the brief acceptance criteria.
/// </summary>
public class ChainCommitRangePhaseTests
{
    private const string StartSha = "1111111111111111111111111111111111111111";
    private const string MidSha = "2222222222222222222222222222222222222222";
    private const string EndSha = "3333333333333333333333333333333333333333";
    private const string WorkDir = "/fake/chain/workdir";

    // -----------------------------------------------------------------------
    // Acceptance: at the first ticket of a chain the range and touched-files are empty
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FirstTicket_ChainStartEqualsCurrentHead_RangeIsEmpty()
    {
        // When no ticket has shipped yet, chainStartSha == currentTargetSha.
        // The helper must return an empty range with no touched files.
        var git = new ChainRangeFakeGit(
            revListCount: 0,
            diffStatFiles: Array.Empty<string>());

        var range = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, StartSha, WorkDir, CancellationToken.None);

        Assert.True(range.IsEmpty);
        Assert.Equal(0, range.CommitCount);
        Assert.Empty(range.TouchedFiles);

        // No git calls needed when anchors are the same.
        Assert.Equal(0, git.RevListCallCount);
        Assert.Equal(0, git.DiffStatCallCount);
    }

    // -----------------------------------------------------------------------
    // Acceptance: for a chain that has shipped M tickets, the helper returns the
    // range bounding exactly those commits and their touched-files
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ShippedOneTicket_RangeContainsOneCommit_TouchedFilesPopulated()
    {
        // After one ticket ships, the target head advances from StartSha to MidSha.
        // The range StartSha..MidSha should contain exactly 1 commit with its files.
        var git = new ChainRangeFakeGit(
            revListCount: 1,
            diffStatFiles: new[] { "src/Feature.cs", "tests/FeatureTests.cs" });

        var range = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, MidSha, WorkDir, CancellationToken.None);

        Assert.False(range.IsEmpty);
        Assert.Equal(1, range.CommitCount);
        Assert.Equal(StartSha, range.StartSha);
        Assert.Equal(MidSha, range.EndSha);
        Assert.Equal(new[] { "src/Feature.cs", "tests/FeatureTests.cs" }, range.TouchedFiles);

        // Both git calls should have used the two-dot range.
        Assert.Equal($"{StartSha}..{MidSha}", git.LastRevListRange);
        Assert.Equal($"{StartSha}..{MidSha}", git.LastDiffStatRange);
    }

    [Fact]
    public async Task ShippedTwoTickets_RangeContainsBothCommits_AllTouchedFilesListed()
    {
        // After two tickets ship, the target head advances from StartSha to EndSha (2 commits).
        var git = new ChainRangeFakeGit(
            revListCount: 2,
            diffStatFiles: new[] { "src/A.cs", "src/B.cs", "tests/ATests.cs", "tests/BTests.cs" });

        var range = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        Assert.False(range.IsEmpty);
        Assert.Equal(2, range.CommitCount);
        Assert.Equal(4, range.TouchedFiles.Count);
        Assert.Equal(StartSha, range.StartSha);
        Assert.Equal(EndSha, range.EndSha);
    }

    // -----------------------------------------------------------------------
    // Acceptance: the computation makes no LLM call and spawns no worker
    // This is a structural property - the helper only calls IGitClient methods.
    // Verified by the fact that ChainRangeFakeGit has no LLM/worker surface at all.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ComputeAsync_IsGitOnlyNoWorkerNoLlm_CompletesSuccessfully()
    {
        // The fake has zero LLM/worker machinery. If ComputeAsync tried to call either
        // it would fail to compile or throw NotImplementedException.
        var git = new ChainRangeFakeGit(
            revListCount: 3,
            diffStatFiles: new[] { "x.cs" });

        var range = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        // Simply completing without exception is the pass condition.
        Assert.Equal(3, range.CommitCount);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    private sealed class ChainRangeFakeGit : IGitClient
    {
        private readonly int _revListCount;
        private readonly IReadOnlyList<string> _diffStatFiles;

        public int RevListCallCount { get; private set; }
        public int DiffStatCallCount { get; private set; }
        public string? LastRevListRange { get; private set; }
        public string? LastDiffStatRange { get; private set; }

        public ChainRangeFakeGit(int revListCount, IReadOnlyList<string> diffStatFiles)
        {
            _revListCount = revListCount;
            _diffStatFiles = diffStatFiles;
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct)
        {
            RevListCallCount++;
            LastRevListRange = range;
            return Task.FromResult(_revListCount);
        }

        public Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct)
        {
            DiffStatCallCount++;
            LastDiffStatRange = range;
            return Task.FromResult(_diffStatFiles);
        }

        // -- Minimal IGitClient stubs (unused by ChainCommitRangeHelper) --

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());

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

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
