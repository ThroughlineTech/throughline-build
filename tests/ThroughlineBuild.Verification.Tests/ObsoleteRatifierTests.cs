using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class ObsoleteRatifierTests
{
    private const string CitedCommit = "abc123def456";
    private const string CitedFile = "src/Foo.cs";
    private const string CitedRationale = "Feature delivered in prior commit";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static WorkerResult MakeEscalateResult(
        string? commit = null,
        string[]? files = null,
        string? rationale = null)
    {
        var c = commit ?? CitedCommit;
        var f = files ?? new[] { CitedFile };
        var r = rationale ?? CitedRationale;
        var fileJson = string.Join(", ", f.Select(x => $"\"{x}\""));
        var escalation = JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "reason": "obsolete",
              "subsumed_by": {
                "commit": "{{c}}",
                "files": [{{fileJson}}],
                "rationale": "{{r}}"
              }
            }
            """);
        return new WorkerResult(
            Status.Escalate,
            "ticket obsolete",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object> { ["escalation"] = escalation });
    }

    private static Ticket MakeTicket() => new Ticket(
        "TLB-1", "uuid-1", "Test Ticket", "feature",
        TicketState.Backlog, Size.S, Risk.Low,
        "<p>Acceptance criteria: feature is implemented.</p>",
        Array.Empty<Relation>(), Array.Empty<string>(), null);

    private static WorkerOptions MakeWorkerOptions() =>
        new WorkerOptions(TimeSpan.FromMinutes(1));

    private static WorkerResult OkVerdictResult(string verdict, string rationale = "looks good") =>
        new WorkerResult(Status.Ok, "verdict", Array.Empty<string>(), null,
            new Dictionary<string, object>
            {
                ["verdict"] = verdict,
                ["rationale"] = rationale
            });

    // Stub IWorkerAgent that returns a configured WorkerResult
    private sealed class StubWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public StubWorkerAgent(WorkerResult result) { _result = result; }
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    // Fake git client where commit lookup succeeds
    private sealed class CommitExistsGit : IGitClient
    {
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(CitedCommit);
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(CitedCommit);
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
    }

    // Fake git client where commit lookup throws (commit not found)
    private sealed class CommitMissingGit : IGitClient
    {
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            throw new InvalidOperationException("unknown revision or path not in the working tree");
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult(CitedCommit);
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
    }

    private static (string dir, IDisposable cleanup) CreateTempDir(params string[] relativeFilePaths)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var rel in relativeFilePaths)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// content");
        }
        return (dir, new TempDirCleanup(dir));
    }

    private sealed class TempDirCleanup : IDisposable
    {
        private readonly string _dir;
        public TempDirCleanup(string dir) { _dir = dir; }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Ratified_CommitExists_FilesExist_ModelPass()
    {
        var (dir, cleanup) = CreateTempDir("src/Foo.cs");
        using (cleanup)
        {
            var worker = new StubWorkerAgent(OkVerdictResult("Pass", "prior work satisfies acceptance criteria"));
            var git = new CommitExistsGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), dir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), null, CancellationToken.None);

            Assert.Equal(VerdictKind.Pass, verdict.Kind);
            Assert.Equal("prior work satisfies acceptance criteria", verdict.Rationale);
        }
    }

    [Fact]
    public async Task Rejected_CommitNotFound_ReturnsFailVerdict()
    {
        var (dir, cleanup) = CreateTempDir("src/Foo.cs");
        using (cleanup)
        {
            // Worker should never be called - commit check fails first
            var worker = new StubWorkerAgent(OkVerdictResult("Pass"));
            var git = new CommitMissingGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), dir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), null, CancellationToken.None);

            Assert.Equal(VerdictKind.Fail, verdict.Kind);
            Assert.Contains(CitedCommit, verdict.Rationale);
        }
    }

    [Fact]
    public async Task Rejected_FileMissingAtHead_ReturnsFailVerdict()
    {
        // Create a temp dir with NO files - cited file is absent
        var (dir, cleanup) = CreateTempDir();
        using (cleanup)
        {
            var worker = new StubWorkerAgent(OkVerdictResult("Pass"));
            var git = new CommitExistsGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), dir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), null, CancellationToken.None);

            Assert.Equal(VerdictKind.Fail, verdict.Kind);
            Assert.Contains(CitedFile, verdict.Rationale);
        }
    }

    [Fact]
    public async Task Rejected_ModelReturnsFail()
    {
        var (dir, cleanup) = CreateTempDir("src/Foo.cs");
        using (cleanup)
        {
            var worker = new StubWorkerAgent(OkVerdictResult("Fail", "missing acceptance criterion"));
            var git = new CommitExistsGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), dir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), null, CancellationToken.None);

            Assert.Equal(VerdictKind.Fail, verdict.Kind);
            Assert.Equal("missing acceptance criterion", verdict.Rationale);
        }
    }

    [Fact]
    public async Task Ratified_EvidenceDirectoryOverride_ResolvesFilesThere()
    {
        // The ratifier is constructed against an EMPTY dir (cited file absent there), but the
        // escalation was raised in a separate worktree that DOES contain the file. Passing that
        // worktree as evidenceDirectory must make Check 2 resolve there - not the construction dir.
        var (ctorDir, ctorCleanup) = CreateTempDir();
        var (worktreeDir, worktreeCleanup) = CreateTempDir("src/Foo.cs");
        using (ctorCleanup)
        using (worktreeCleanup)
        {
            var worker = new StubWorkerAgent(OkVerdictResult("Pass", "prior work satisfies acceptance criteria"));
            var git = new CommitExistsGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), ctorDir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), worktreeDir, CancellationToken.None);

            Assert.Equal(VerdictKind.Pass, verdict.Kind);
        }
    }

    [Fact]
    public async Task Rejected_ModelReturnsRework()
    {
        var (dir, cleanup) = CreateTempDir("src/Foo.cs");
        using (cleanup)
        {
            var worker = new StubWorkerAgent(OkVerdictResult("Rework", "needs revision"));
            var git = new CommitExistsGit();
            var ratifier = new ObsoleteRatifier(worker, MakeWorkerOptions(), dir, git);

            var verdict = await ratifier.RatifyAsync(MakeTicket(), MakeEscalateResult(), null, CancellationToken.None);

            Assert.Equal(VerdictKind.Rework, verdict.Kind);
            Assert.Equal("needs revision", verdict.Rationale);
        }
    }
}
