using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class GateControlProberTests
{
    private sealed class FixedChecksRunner : AutomatedChecksRunner
    {
        private readonly IReadOnlyList<CheckResult> _results;

        public FixedChecksRunner(IReadOnlyList<CheckResult> results) => _results = results;

        public int CallCount { get; private set; }

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_results);
        }
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly HashSet<string> _trackedPaths;

        public FakeGitClient(params string[] trackedPaths) =>
            _trackedPaths = new HashSet<string>(trackedPaths, StringComparer.Ordinal);

        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("base-sha");

        public Task<WorktreeCreateResult> CreateWorktreeAsync(
            string worktreePath,
            string newBranch,
            string fromRef,
            string mainWorktreePath,
            CancellationToken ct) =>
            Task.FromResult(new WorktreeCreateResult(true, null, worktreePath));

        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct) =>
            Task.FromResult(new WorktreeRemoveResult(true, null));

        public Task<GitOpResult> DeleteBranchAsync(string branch, bool force, string mainWorktreePath, CancellationToken ct) =>
            Task.FromResult(new GitOpResult(true, null));

        public Task<IReadOnlyList<string>> FilterTrackedPathsAsync(
            IReadOnlyList<string> paths,
            string workingDirectory,
            CancellationToken ct)
        {
            var matches = new List<string>();
            foreach (var path in paths)
            {
                if (_trackedPaths.Contains(path))
                {
                    matches.Add(path);
                    continue;
                }

                var prefix = path.TrimEnd('/') + "/";
                var child = _trackedPaths.FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal));
                if (child is not null)
                    matches.Add(child);
            }

            return Task.FromResult<IReadOnlyList<string>>(matches);
        }

        // Remaining members are not exercised by the prober but are abstract on IGitClient.
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
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
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    [Fact]
    public async Task ProbeAsync_MissingRequiredPaths_ReturnsInconclusiveWithoutRunningCheck()
    {
        var spec = new CheckSpec(
            "test",
            "npm",
            new[] { "test" },
            TimeSpan.FromMinutes(1),
            RequiredPaths: new[] { "package.json", "src" });
        var runner = new FixedChecksRunner(new[] { Failed(spec) });
        var git = new FakeGitClient();
        var prober = new GateControlProber();

        var verdict = await prober.ProbeAsync(new[] { spec }, "main", "repo", runner, git, CancellationToken.None);

        Assert.Equal(GateControlOutcome.Inconclusive, verdict.Outcome);
        Assert.Equal(0, runner.CallCount);
        Assert.Contains("test", verdict.Detail);
        Assert.Contains("package.json", verdict.Detail);
        Assert.Contains("src", verdict.Detail);
    }

    [Fact]
    public async Task ProbeAsync_RequiredPathsPresentAndCheckFails_ReturnsBaseFails()
    {
        var spec = new CheckSpec(
            "test",
            "npm",
            new[] { "test" },
            TimeSpan.FromMinutes(1),
            RequiredPaths: new[] { "package.json", "src" });
        var runner = new FixedChecksRunner(new[] { Failed(spec) });
        var git = new FakeGitClient("package.json", "src/App.ts");
        var prober = new GateControlProber();

        var verdict = await prober.ProbeAsync(new[] { spec }, "main", "repo", runner, git, CancellationToken.None);

        Assert.Equal(GateControlOutcome.BaseFails, verdict.Outcome);
        Assert.Equal(1, runner.CallCount);
        Assert.Single(verdict.CheckResults);
    }

    [Fact]
    public async Task ProbeAsync_MissingSetupRequiredPath_ReturnsInconclusiveWithoutRunningCheck()
    {
        var spec = new CheckSpec(
            "xcodegen",
            "xcodegen",
            new[] { "generate" },
            TimeSpan.FromMinutes(1),
            Role: CheckRole.Setup,
            RequiredPaths: new[] { "project.yml" });
        var runner = new FixedChecksRunner(new[] { Failed(spec) });
        var git = new FakeGitClient();
        var prober = new GateControlProber();

        var verdict = await prober.ProbeAsync(new[] { spec }, "main", "repo", runner, git, CancellationToken.None);

        Assert.Equal(GateControlOutcome.Inconclusive, verdict.Outcome);
        Assert.Equal(0, runner.CallCount);
        Assert.Contains("project.yml", verdict.Detail);
    }

    [Fact]
    public async Task ProbeAsync_MissingCanaryParentFallsBackToInconclusiveForLegacySpecs()
    {
        var spec = new CheckSpec(
            "typecheck",
            "swift",
            new[] { "test" },
            TimeSpan.FromMinutes(1),
            Canary: new[] { new CanaryFile("Sources/App/__tlb_probe.swift", "let x: Int = \"s\"") });
        var runner = new FixedChecksRunner(new[] { Failed(spec) });
        var git = new FakeGitClient();
        var prober = new GateControlProber();

        var verdict = await prober.ProbeAsync(new[] { spec }, "main", "repo", runner, git, CancellationToken.None);

        Assert.Equal(GateControlOutcome.Inconclusive, verdict.Outcome);
        Assert.Equal(0, runner.CallCount);
        Assert.Contains("Sources/App", verdict.Detail);
    }

    private static CheckResult Failed(CheckSpec spec) =>
        new(spec.Name, false, 1, "", "failed", TimeSpan.FromMilliseconds(1), spec.Role);
}
