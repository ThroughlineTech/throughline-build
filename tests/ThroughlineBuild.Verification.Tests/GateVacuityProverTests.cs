using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class GateVacuityProverTests
{
    // --- Test doubles ---------------------------------------------------------

    // A runner whose Passed verdict is decided by a supplied predicate over the worktree path,
    // so a test can make Passed depend on whether the canary file exists on disk. Also counts
    // how many times RunAsync was invoked (to assert per-check-once does not re-run).
    private sealed class FakeChecksRunner : AutomatedChecksRunner
    {
        private readonly Func<string, bool> _passed;
        public int CallCount { get; private set; }

        public FakeChecksRunner(Func<string, bool> passed) => _passed = passed;

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct)
        {
            CallCount++;
            var spec = specs[0];
            bool passed = _passed(workingDirectory);
            var result = new CheckResult(spec.Name, passed, passed ? 0 : 1, "", "", TimeSpan.FromMilliseconds(1), spec.Role);
            return Task.FromResult<IReadOnlyList<CheckResult>>(new[] { result });
        }
    }

    // A runner that throws when invoked, to prove the canary is still cleaned up.
    private sealed class ThrowingChecksRunner : AutomatedChecksRunner
    {
        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs, string workingDirectory, CancellationToken ct)
            => throw new InvalidOperationException("runner boom");
    }

    // Minimal IGitClient: only GetUntrackedFilesAsync is meaningful (settable list, defaults empty).
    // All other members fall through to the interface's default implementations.
    private sealed class FakeGitClient : IGitClient
    {
        public IReadOnlyList<string> Untracked { get; set; } = Array.Empty<string>();

        public Task<IReadOnlyList<string>> GetUntrackedFilesAsync(string workingDirectory, CancellationToken ct)
            => Task.FromResult(Untracked);

        // Remaining members are not exercised by the prover but are abstract on IGitClient.
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("");
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string worktreePath, string newBranch, string fromRef, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) =>
            Task.FromResult("");
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

    // --- Helpers --------------------------------------------------------------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb-vacuity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CheckSpec SpecWithCanary(string name, string canaryPath, string content)
        => new CheckSpec(
            name, "noop", Array.Empty<string>(), TimeSpan.FromSeconds(30),
            CheckRole.Gating,
            new[] { new CanaryFile(canaryPath, content) });

    // --- Tests ----------------------------------------------------------------

    // 1. VACUOUS: runner returns Passed=true regardless of files -> Vacuous; reason names the
    //    check and canary path; the canary file is deleted afterward.
    [Fact]
    public async Task Vacuous_WhenCheckPassesWithCanaryPresent()
    {
        var worktree = NewTempDir();
        try
        {
            var spec = SpecWithCanary("typecheck", "probe/__tlb_probe.ts", "const x: number = \"s\";");
            var runner = new FakeChecksRunner(_ => true); // passes regardless of canary
            var git = new FakeGitClient();
            var prover = new GateVacuityProver();

            var verdict = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);

            Assert.Equal(GateVacuityOutcome.Vacuous, verdict.Outcome);
            Assert.Equal("typecheck", verdict.CheckName);
            Assert.NotNull(verdict.Reason);
            Assert.Contains("typecheck", verdict.Reason!);
            Assert.Contains("probe/__tlb_probe.ts", verdict.Reason!);

            var full = Path.Combine(worktree, "probe", "__tlb_probe.ts");
            Assert.False(File.Exists(full), "canary should be deleted after probing");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 2. OK: runner returns Passed=false ONLY WHEN the canary exists on disk (proving the prover
    //    materialized it before running), Passed=true otherwise -> Ok; canary deleted afterward.
    [Fact]
    public async Task Ok_WhenCheckRejectsCanaryItMaterialized()
    {
        var worktree = NewTempDir();
        try
        {
            var canaryRel = "probe/__tlb_probe.ts";
            var canaryFull = Path.Combine(worktree, "probe", "__tlb_probe.ts");
            var spec = SpecWithCanary("typecheck", canaryRel, "const x: number = \"s\";");
            // fail (Passed=false) only if the canary is present on disk at run time
            var runner = new FakeChecksRunner(wt => !File.Exists(Path.Combine(wt, "probe", "__tlb_probe.ts")));
            var git = new FakeGitClient();
            var prover = new GateVacuityProver();

            var verdict = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);

            Assert.Equal(GateVacuityOutcome.Ok, verdict.Outcome);
            Assert.Null(verdict.Reason);
            Assert.False(File.Exists(canaryFull), "canary should be deleted after probing");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 3. MATERIALIZE-THEN-DELETE around a throwing runner: ProveAsync rethrows, but the canary
    //    is still deleted (finally cleans up before the exception propagates).
    [Fact]
    public async Task ThrowingRunner_Rethrows_ButCanaryIsDeleted()
    {
        var worktree = NewTempDir();
        var canaryFull = Path.Combine(worktree, "probe", "__tlb_probe.ts");
        try
        {
            var spec = SpecWithCanary("typecheck", "probe/__tlb_probe.ts", "const x: number = \"s\";");
            var runner = new ThrowingChecksRunner();
            var git = new FakeGitClient();
            var prover = new GateVacuityProver();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None));

            Assert.False(File.Exists(canaryFull), "canary should be deleted even when the runner throws");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 4. CLEANUP CLEAN -> OK: git reports nothing untracked, runner rejects the canary -> Ok.
    [Fact]
    public async Task CleanupClean_ReturnsOk()
    {
        var worktree = NewTempDir();
        try
        {
            var spec = SpecWithCanary("typecheck", "probe/__tlb_probe.ts", "const x: number = \"s\";");
            var runner = new FakeChecksRunner(wt => !File.Exists(Path.Combine(wt, "probe", "__tlb_probe.ts")));
            var git = new FakeGitClient { Untracked = Array.Empty<string>() };
            var prover = new GateVacuityProver();

            var verdict = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);

            Assert.Equal(GateVacuityOutcome.Ok, verdict.Outcome);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 5. CLEANUP FAILED -> CleanupFailed: git reports the canary's relative path as untracked
    //    (simulating a survivor); the runner rejects the canary (probe verdict would be Ok) ->
    //    the git-hygiene assert overrides Ok with CleanupFailed.
    [Fact]
    public async Task CleanupFailed_WhenCanaryReportedUntracked_OverridesOk()
    {
        var worktree = NewTempDir();
        try
        {
            var canaryRel = "probe/__tlb_probe.ts";
            var spec = SpecWithCanary("typecheck", canaryRel, "const x: number = \"s\";");
            var runner = new FakeChecksRunner(wt => !File.Exists(Path.Combine(wt, "probe", "__tlb_probe.ts")));
            var git = new FakeGitClient { Untracked = new[] { canaryRel } };
            var prover = new GateVacuityProver();

            var verdict = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);

            Assert.Equal(GateVacuityOutcome.CleanupFailed, verdict.Outcome);
            Assert.NotNull(verdict.Reason);
            Assert.Contains(canaryRel, verdict.Reason!);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 6. UNVERIFIED: a gating check with Canary == null -> Unverified, no exception; a SECOND
    //    call for the same check name -> AlreadyProven.
    [Fact]
    public async Task NoCanary_ReturnsUnverified_ThenAlreadyProven()
    {
        var spec = new CheckSpec("typecheck", "noop", Array.Empty<string>(), TimeSpan.FromSeconds(30), CheckRole.Gating, Canary: null);
        var runner = new FakeChecksRunner(_ => false);
        var git = new FakeGitClient();
        var prover = new GateVacuityProver();

        var first = await prover.ProveAsync(spec, runner, git, "ignored", CancellationToken.None);
        Assert.Equal(GateVacuityOutcome.Unverified, first.Outcome);
        Assert.NotNull(first.Reason);
        Assert.Contains("typecheck", first.Reason!);

        var second = await prover.ProveAsync(spec, runner, git, "ignored", CancellationToken.None);
        Assert.Equal(GateVacuityOutcome.AlreadyProven, second.Outcome);
        Assert.Null(second.Reason);
    }

    // 7. PER-CHECK-ONCE: probing the same spec.Name twice -> first returns its real verdict,
    //    second returns AlreadyProven WITHOUT re-running the runner (assert via call-count).
    [Fact]
    public async Task SameCheckName_ProbedOnce_SecondCallIsAlreadyProven_RunnerNotReRun()
    {
        var worktree = NewTempDir();
        try
        {
            var spec = SpecWithCanary("typecheck", "probe/__tlb_probe.ts", "const x: number = \"s\";");
            var runner = new FakeChecksRunner(_ => true); // first probe is Vacuous
            var git = new FakeGitClient();
            var prover = new GateVacuityProver();

            var first = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);
            Assert.Equal(GateVacuityOutcome.Vacuous, first.Outcome);
            Assert.Equal(1, runner.CallCount);

            var second = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);
            Assert.Equal(GateVacuityOutcome.AlreadyProven, second.Outcome);
            Assert.Equal(1, runner.CallCount); // runner was NOT invoked again
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    // 8. SECOND-STACK SMOKE: a canary that looks like a DIFFERENT stack (a .py file, exe "mypy")
    //    drives the same data-driven Vacuous behavior. Documents the mechanism is not TS-specific.
    [Fact]
    public async Task SecondStackSmoke_Vacuous_WithPythonShapedCanary()
    {
        var worktree = NewTempDir();
        try
        {
            var spec = new CheckSpec(
                "typecheck", "mypy", new[] { "." }, TimeSpan.FromSeconds(30),
                CheckRole.Gating,
                new[] { new CanaryFile("probe/__tlb_probe.py", "x: int = \"s\"") });
            var runner = new FakeChecksRunner(_ => true); // passes despite the broken canary
            var git = new FakeGitClient();
            var prover = new GateVacuityProver();

            var verdict = await prover.ProveAsync(spec, runner, git, worktree, CancellationToken.None);

            Assert.Equal(GateVacuityOutcome.Vacuous, verdict.Outcome);
            Assert.Contains("probe/__tlb_probe.py", verdict.Reason!);

            var full = Path.Combine(worktree, "probe", "__tlb_probe.py");
            Assert.False(File.Exists(full), "python-shaped canary should be deleted after probing");
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }
}
