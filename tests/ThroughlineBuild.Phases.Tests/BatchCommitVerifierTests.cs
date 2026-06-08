using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

/// <summary>
/// Unit tests for BatchCommitVerifier. Each test targets one of the four
/// acceptance criteria from TLB-448:
///   AC1 - all commits exist in declared order -> accepted, produces mapping
///   AC2 - commit absent from branch -> rejected, names the ticket
///   AC3 - commits out of declared order -> rejected
///   AC4 - dirty worktree -> rejected with clear reason
/// </summary>
public class BatchCommitVerifierTests
{
    // Minimal fake: only overrides the two methods the verifier actually calls.
    // All other methods use their IGitClient default implementations (empty / success).
    private sealed class FakeGit : IGitClient
    {
        public IReadOnlyList<string> TrackedChanges { get; set; } =
            Array.Empty<string>();

        public IReadOnlyList<string> LogShas { get; set; } =
            Array.Empty<string>();

        // IGitClient required methods (no defaults).
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("abc000");

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult("head000");

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
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        // Optional overrides used by BatchCommitVerifier.
        public Task<IReadOnlyList<string>> GetTrackedChangesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult(TrackedChanges);

        public Task<IReadOnlyList<string>> LogShasAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(LogShas);
    }

    // -------------------------------------------------------------------------
    // AC1: all commits exist in declared order -> success + confirmed mapping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_AllCommitsExistInDeclaredOrder_ReturnsSuccess()
    {
        var git = new FakeGit
        {
            // "bbb111" (stack_pos 1, newest HEAD) then "aaa000" (stack_pos 0, oldest).
            LogShas = new[] { "bbb111", "aaa000" }
        };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.Equal(2, result.ConfirmedTickets.Count);
        // ConfirmedTickets are sorted by stack_position ascending.
        Assert.Equal("TLB-2", result.ConfirmedTickets[0].TicketId);
        Assert.Equal("TLB-3", result.ConfirmedTickets[1].TicketId);
    }

    [Fact]
    public async Task VerifyAsync_SingleTicket_Accepted()
    {
        var git = new FakeGit { LogShas = new[] { "aaa000" } };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.ConfirmedTickets);
    }

    [Fact]
    public async Task VerifyAsync_NoReportedTickets_ReturnsSuccessWithEmptyList()
    {
        var git = new FakeGit();

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.ConfirmedTickets);
    }

    [Fact]
    public async Task VerifyAsync_EmptyTicketsList_ReturnsSuccessWithEmptyList()
    {
        var git = new FakeGit();

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", Array.Empty<BatchTicketResult>(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.ConfirmedTickets);
    }

    // -------------------------------------------------------------------------
    // AC2: commit absent from branch -> rejected, names the failing ticket
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_CommitAbsentFromBranch_ReturnsFailureNamingTicket()
    {
        var git = new FakeGit
        {
            // Only TLB-3's commit is present; TLB-2's "aaa000" is absent.
            LogShas = new[] { "bbb111" }
        };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
            new("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("TLB-2", result.FailureReason);
        Assert.Contains("aaa000", result.FailureReason);
    }

    [Fact]
    public async Task VerifyAsync_GitLogEmpty_TicketsReported_FailsClosed()
    {
        // git log returns empty: cannot confirm any SHAs; reject rather than pass through.
        var git = new FakeGit { LogShas = Array.Empty<string>() };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    // -------------------------------------------------------------------------
    // AC3: commits out of declared order -> rejected, names the failing ticket
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_CommitsOutOfDeclaredOrder_ReturnsFailureNamingTicket()
    {
        var git = new FakeGit
        {
            // "aaa000" newest (stack_pos 0 in worker claim but git says it is HEAD),
            // "bbb111" older - contradicts stack_pos 1 for TLB-3 being the newest commit.
            LogShas = new[] { "aaa000", "bbb111" }
        };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"), // stack_pos 0 = oldest
            new("TLB-3", "bbb111", 1, Array.Empty<string>(), "SUMMARY_3"), // stack_pos 1 = newest
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        // TLB-3 (stack_pos 1) should be newer than TLB-2 (stack_pos 0) but in the log
        // "bbb111" (TLB-3) is older than "aaa000" (TLB-2). Verifier must name TLB-3.
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("TLB-3", result.FailureReason);
    }

    // -------------------------------------------------------------------------
    // AC4: dirty worktree -> rejected with clear reason
    // -------------------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_DirtyWorktree_ReturnsFailureWithDirtyReason()
    {
        var git = new FakeGit
        {
            TrackedChanges = new[] { "src/Uncommitted.cs" }
        };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("dirty", result.FailureReason);
        Assert.Contains("src/Uncommitted.cs", result.FailureReason);
    }

    [Fact]
    public async Task VerifyAsync_DirtyWorktree_RejectsBeforeCheckingCommits()
    {
        // Even with a valid log, a dirty tree must be rejected immediately.
        var git = new FakeGit
        {
            TrackedChanges = new[] { "src/Leftover.cs" },
            LogShas = new[] { "aaa000" }
        };
        var tickets = new List<BatchTicketResult>
        {
            new("TLB-2", "aaa000", 0, Array.Empty<string>(), "SUMMARY_2"),
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", tickets, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("dirty", result.FailureReason);
    }

    [Fact]
    public async Task VerifyAsync_DirtyWorktree_ListsUpToFivePaths()
    {
        var git = new FakeGit
        {
            TrackedChanges = new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs" }
        };

        var result = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", null, CancellationToken.None);

        Assert.False(result.Success);
        // Six dirty files: first five listed, sixth summarized as "(+1 more)".
        Assert.Contains("a.cs", result.FailureReason);
        Assert.Contains("+1 more", result.FailureReason);
    }

    // -------------------------------------------------------------------------
    // ReconstructFromGitAsync: rebuild per-ticket attribution from git when the
    // worker succeeded but omitted its self-reported `tickets` array.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reconstruct_OneCommitPerTicket_MapsInStackOrderOldestFirst()
    {
        // LogShas is newest-first: "ccc222" is HEAD, "aaa000" is oldest.
        var git = new FakeGit { LogShas = new[] { "ccc222", "bbb111", "aaa000" } };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main",
            new[] { "TLB-3", "TLB-4", "TLB-5" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.Equal(3, result.ConfirmedTickets.Count);

        // Declared order maps oldest -> stack_position 1, HEAD -> last.
        var first = result.ConfirmedTickets[0];
        Assert.Equal("TLB-3", first.TicketId);
        Assert.Equal("aaa000", first.CommitSha);
        Assert.Equal(1, first.StackPosition);
        Assert.Equal("IMPLEMENT_SUMMARY_1", first.SummaryRef);

        Assert.Equal("TLB-4", result.ConfirmedTickets[1].TicketId);
        Assert.Equal("bbb111", result.ConfirmedTickets[1].CommitSha);
        Assert.Equal(2, result.ConfirmedTickets[1].StackPosition);

        Assert.Equal("TLB-5", result.ConfirmedTickets[2].TicketId);
        Assert.Equal("ccc222", result.ConfirmedTickets[2].CommitSha);
        Assert.Equal(3, result.ConfirmedTickets[2].StackPosition);
    }

    [Fact]
    public async Task Reconstruct_OutputPassesVerifyAsync_EndToEnd()
    {
        // The reconstructed list must satisfy VerifyAsync against the same git state,
        // since that is exactly how ChainPhase feeds it back into the verify path.
        var git = new FakeGit { LogShas = new[] { "ccc222", "bbb111", "aaa000" } };

        var recon = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main",
            new[] { "TLB-3", "TLB-4", "TLB-5" }, CancellationToken.None);

        var verify = await BatchCommitVerifier.VerifyAsync(
            git, "/wt", "origin/main", recon.ConfirmedTickets, CancellationToken.None);

        Assert.True(verify.Success);
        Assert.Equal(3, verify.ConfirmedTickets.Count);
    }

    [Fact]
    public async Task Reconstruct_SingleTicketSingleCommit_Succeeds()
    {
        var git = new FakeGit { LogShas = new[] { "aaa000" } };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main", new[] { "TLB-3" }, CancellationToken.None);

        Assert.True(result.Success);
        var only = Assert.Single(result.ConfirmedTickets);
        Assert.Equal("TLB-3", only.TicketId);
        Assert.Equal("aaa000", only.CommitSha);
        Assert.Equal(1, only.StackPosition);
    }

    [Fact]
    public async Task Reconstruct_CommitCountExceedsTicketCount_FailsRatherThanGuess()
    {
        // Four commits but three tickets: the one-commit-per-ticket assumption is broken,
        // so attribution is ambiguous and must fail loudly instead of mis-attributing.
        var git = new FakeGit { LogShas = new[] { "ddd333", "ccc222", "bbb111", "aaa000" } };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main",
            new[] { "TLB-3", "TLB-4", "TLB-5" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("commit count (4)", result.FailureReason);
        Assert.Contains("ticket count (3)", result.FailureReason);
        Assert.Empty(result.ConfirmedTickets);
    }

    [Fact]
    public async Task Reconstruct_FewerCommitsThanTickets_Fails()
    {
        var git = new FakeGit { LogShas = new[] { "bbb111", "aaa000" } };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main",
            new[] { "TLB-3", "TLB-4", "TLB-5" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("commit count (2)", result.FailureReason);
        Assert.Contains("ticket count (3)", result.FailureReason);
    }

    [Fact]
    public async Task Reconstruct_NoCommitsSinceBase_Fails()
    {
        var git = new FakeGit { LogShas = Array.Empty<string>() };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main", new[] { "TLB-3" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no commits since base", result.FailureReason);
    }

    [Fact]
    public async Task Reconstruct_NoDeclaredTickets_ReturnsEmptySuccess()
    {
        var git = new FakeGit { LogShas = new[] { "aaa000" } };

        var result = await BatchCommitVerifier.ReconstructFromGitAsync(
            git, "/wt", "origin/main", Array.Empty<string>(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.ConfirmedTickets);
    }
}
