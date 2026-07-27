using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

/// <summary>
/// Unit tests for ChainCommitRangeHelper (Brief 08 of Plan C).
///
/// Acceptance:
///   - For a chain that has shipped M tickets, the helper returns the range bounding
///     exactly those commits and their touched-files.
///   - At the first ticket of a chain, the range and touched-files are empty.
///   - The computation makes no LLM call and spawns no worker.
/// </summary>
public class ChainCommitRangeTests
{
    private const string StartSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EndSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WorkDir = "/fake/workdir";

    // -----------------------------------------------------------------------
    // Empty-range (first ticket): anchors equal
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ComputeAsync_AnchorsSame_ReturnsEmptyRange()
    {
        var git = new FakeGitForRange(
            revListCount: 0,
            diffStatFiles: Array.Empty<string>());

        var result = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, StartSha, WorkDir, CancellationToken.None);

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.CommitCount);
        Assert.Empty(result.TouchedFiles);
        Assert.Equal(StartSha, result.StartSha);
        Assert.Equal(StartSha, result.EndSha);
    }

    [Fact]
    public async Task ComputeAsync_AnchorsDiffer_RevListReturnsZero_ReturnsEmptyRange()
    {
        // Anchors differ but git says 0 commits in range (e.g. start is ahead of end).
        var git = new FakeGitForRange(
            revListCount: 0,
            diffStatFiles: new[] { "src/Foo.cs" });

        var result = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.CommitCount);
        Assert.Empty(result.TouchedFiles);
    }

    // -----------------------------------------------------------------------
    // Non-empty range (M tickets shipped)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ComputeAsync_TwoCommitsInRange_ReturnsBoundingRangeAndTouchedFiles()
    {
        var git = new FakeGitForRange(
            revListCount: 2,
            diffStatFiles: new[] { "src/A.cs", "src/B.cs", "tests/ATests.cs" });

        var result = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        Assert.False(result.IsEmpty);
        Assert.Equal(2, result.CommitCount);
        Assert.Equal(StartSha, result.StartSha);
        Assert.Equal(EndSha, result.EndSha);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs", "tests/ATests.cs" }, result.TouchedFiles);
    }

    [Fact]
    public async Task ComputeAsync_DuplicateFilesInDiffStat_DeduplicatesPreservingFirstOrder()
    {
        var git = new FakeGitForRange(
            revListCount: 3,
            diffStatFiles: new[] { "src/Foo.cs", "src/Bar.cs", "src/Foo.cs", "src/Baz.cs", "src/Bar.cs" });

        var result = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        // Duplicates removed; first-encounter order preserved.
        Assert.Equal(new[] { "src/Foo.cs", "src/Bar.cs", "src/Baz.cs" }, result.TouchedFiles);
        Assert.Equal(3, result.CommitCount);
    }

    [Fact]
    public async Task ComputeAsync_UsesCorrectTwoDotRange()
    {
        // Verify the range passed to git calls is exactly "startSha..endSha".
        var git = new CapturingFakeGitForRange(revListCount: 1, diffStatFiles: new[] { "src/X.cs" });

        await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        var expectedRange = $"{StartSha}..{EndSha}";
        Assert.Equal(expectedRange, git.CapturedRevListRange);
        Assert.Equal(expectedRange, git.CapturedDiffStatRange);
    }

    // -----------------------------------------------------------------------
    // Fault tolerance: git failure returns empty
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ComputeAsync_GitDiffStatFails_ReturnsRangeWithZeroFiles()
    {
        // RevListCount succeeds (5 commits) but DiffStatFiles returns empty on failure.
        var git = new FakeGitForRange(
            revListCount: 5,
            diffStatFiles: Array.Empty<string>());

        var result = await ChainCommitRangeHelper.ComputeAsync(
            git, StartSha, EndSha, WorkDir, CancellationToken.None);

        Assert.Equal(5, result.CommitCount);
        Assert.Empty(result.TouchedFiles);
        Assert.False(result.IsEmpty);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    private sealed class FakeGitForRange : IGitClient
    {
        private readonly int _revListCount;
        private readonly IReadOnlyList<string> _diffStatFiles;

        public FakeGitForRange(int revListCount, IReadOnlyList<string> diffStatFiles)
        {
            _revListCount = revListCount;
            _diffStatFiles = diffStatFiles;
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_revListCount);

        public Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(_diffStatFiles);

        // IGitClient default implementations handle the rest.
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

    /// <summary>Captures the range arguments passed to RevListCountAsync and DiffStatFilesAsync.</summary>
    private sealed class CapturingFakeGitForRange : IGitClient
    {
        private readonly int _revListCount;
        private readonly IReadOnlyList<string> _diffStatFiles;

        public string? CapturedRevListRange { get; private set; }
        public string? CapturedDiffStatRange { get; private set; }

        public CapturingFakeGitForRange(int revListCount, IReadOnlyList<string> diffStatFiles)
        {
            _revListCount = revListCount;
            _diffStatFiles = diffStatFiles;
        }

        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct)
        {
            CapturedRevListRange = range;
            return Task.FromResult(_revListCount);
        }

        public Task<IReadOnlyList<string>> DiffStatFilesAsync(string range, string workingDirectory, CancellationToken ct)
        {
            CapturedDiffStatRange = range;
            return Task.FromResult(_diffStatFiles);
        }

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
