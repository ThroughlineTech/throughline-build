using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

internal sealed class FakeGitClient : IGitClient
{
    public List<WorktreeInfo> Worktrees { get; set; } = new();
    public WorktreeRemoveResult RemoveResult { get; set; } = new(true, null);
    public WorktreeRemoveResult RemoveForceResult { get; set; } = new(true, null);

    public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct)
        => Task.FromResult("abc123");

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WorktreeInfo>>(Worktrees);

    public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        => Task.FromResult(force ? RemoveForceResult : RemoveResult);

    public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct)
        => Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

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

internal sealed class FakeProcessKiller : IProcessKiller
{
    public List<int> KilledPids { get; } = new();

    public bool TryKill(int pid, out string? notes)
    {
        KilledPids.Add(pid);
        notes = null;
        return true;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class WorktreeDecrufterTests
{
    private static WorktreeInfo MakeWorktreeInfo(string path) =>
        new WorktreeInfo(path, "main", "abc123", false, false);

    [Fact]
    public async Task DecruftAsync_WorktreeNotInList_Halts_NoDestruction()
    {
        var git = new FakeGitClient { Worktrees = new() };
        var killer = new FakeProcessKiller();
        var decrufter = new WorktreeDecrufter(git, killer);

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(DecruftStep.WorktreeNotFound, result.HaltedAt);
        Assert.Empty(result.Outcomes);
        Assert.Empty(killer.KilledPids);
    }

    [Fact]
    public async Task DecruftAsync_CleanWorktree_RemoveSucceeds_StopsAtStep4()
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var git = new FakeGitClient
        {
            Worktrees = new() { MakeWorktreeInfo(worktreePath) },
            RemoveResult = new(true, null)
        };
        var killer = new FakeProcessKiller();
        var decrufter = new WorktreeDecrufter(git, killer);

        var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

        Assert.Null(result.HaltedAt);
        Assert.True(result.Outcomes.ContainsKey(DecruftStep.GitWorktreeRemove));
        Assert.True(result.Outcomes[DecruftStep.GitWorktreeRemove].Success);
        Assert.False(result.Outcomes.ContainsKey(DecruftStep.GitWorktreeRemoveForce));
        Assert.False(result.Outcomes.ContainsKey(DecruftStep.DirectoryDelete));
        Assert.False(result.Outcomes.ContainsKey(DecruftStep.GitWorktreePrune));
    }

    [Fact]
    public async Task DecruftAsync_WithPreviewPid_KillsPids()
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            // Write a .preview.pid file with one valid line and one skip line
            var pidFile = Path.Combine(worktreePath, ".preview.pid");
            await File.WriteAllLinesAsync(pidFile, new[]
            {
                "preview 9999 some-service",   // valid: field[1]=9999
                "preview - unused"             // skip: field[1]='-'
            });

            var git = new FakeGitClient
            {
                Worktrees = new() { MakeWorktreeInfo(worktreePath) },
                RemoveResult = new(true, null)
            };
            var killer = new FakeProcessKiller();
            var decrufter = new WorktreeDecrufter(git, killer);

            var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

            Assert.True(result.Outcomes.ContainsKey(DecruftStep.KillPreviewPids));
            Assert.True(result.Outcomes[DecruftStep.KillPreviewPids].Attempted);
            Assert.Contains(9999, killer.KilledPids);
            Assert.Single(killer.KilledPids);
        }
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, true);
        }
    }

    [Fact]
    public async Task DecruftAsync_RemoveFailsForceSucceeds_StopsAtStep5()
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var git = new FakeGitClient
        {
            Worktrees = new() { MakeWorktreeInfo(worktreePath) },
            RemoveResult = new(false, "has uncommitted changes"),
            RemoveForceResult = new(true, null)
        };
        var killer = new FakeProcessKiller();
        var decrufter = new WorktreeDecrufter(git, killer);

        var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

        Assert.Null(result.HaltedAt);
        Assert.True(result.Outcomes.ContainsKey(DecruftStep.GitWorktreeRemove));
        Assert.False(result.Outcomes[DecruftStep.GitWorktreeRemove].Success);
        Assert.True(result.Outcomes.ContainsKey(DecruftStep.GitWorktreeRemoveForce));
        Assert.True(result.Outcomes[DecruftStep.GitWorktreeRemoveForce].Success);
        Assert.False(result.Outcomes.ContainsKey(DecruftStep.DirectoryDelete));
        Assert.False(result.Outcomes.ContainsKey(DecruftStep.GitWorktreePrune));
    }

    [Fact]
    public async Task DecruftAsync_NodeModulesJunction_JunctionRemovedBeforeWorktreeRemove()
    {
        if (!OperatingSystem.IsWindows()) return;

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var targetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        Directory.CreateDirectory(targetPath);
        var nodeModules = Path.Combine(worktreePath, "node_modules");
        Directory.CreateDirectory(nodeModules);
        var junctionPath = Path.Combine(nodeModules, "some-dep");
        try
        {
            try { Directory.CreateSymbolicLink(junctionPath, targetPath); }
            catch (UnauthorizedAccessException) { return; } // no symlink privilege - skip

            var git = new FakeGitClient
            {
                Worktrees = new() { MakeWorktreeInfo(worktreePath) },
                RemoveResult = new(true, null)
            };
            var decrufter = new WorktreeDecrufter(git, new FakeProcessKiller());

            var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

            Assert.True(result.Outcomes[DecruftStep.PreCleanReparsePoints].Success);
            Assert.False(Directory.Exists(junctionPath));
            Assert.True(Directory.Exists(targetPath));
        }
        finally
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, true);
        }
    }

    [Fact]
    public async Task DecruftAsync_BothFail_DirectoryDeleteRuns()
    {
        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            var git = new FakeGitClient
            {
                Worktrees = new() { MakeWorktreeInfo(worktreePath) },
                RemoveResult = new(false, "fail"),
                RemoveForceResult = new(false, "fail force")
            };
            var killer = new FakeProcessKiller();
            var decrufter = new WorktreeDecrufter(git, killer);

            // Use a real main worktree path that exists so git prune can run
            // (we don't care if prune succeeds, just that Directory.Delete ran)
            var result = await decrufter.DecruftAsync(worktreePath, Path.GetTempPath(), CancellationToken.None);

            Assert.True(result.Outcomes.ContainsKey(DecruftStep.DirectoryDelete));
            Assert.True(result.Outcomes[DecruftStep.DirectoryDelete].Attempted);
            Assert.False(Directory.Exists(worktreePath));
        }
        finally
        {
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, true);
        }
    }
}
