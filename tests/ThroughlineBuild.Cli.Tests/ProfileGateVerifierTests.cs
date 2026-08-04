using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class ProfileGateVerifierTests
{
    private sealed class AlwaysGreenRunner : AutomatedChecksRunner
    {
        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct) => Results(specs);

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct,
            RequiredPathHandling requiredPathHandling) => Results(specs);

        private static Task<IReadOnlyList<CheckResult>> Results(IReadOnlyList<CheckSpec> specs) =>
            Task.FromResult<IReadOnlyList<CheckResult>>(specs.Select(spec =>
                new CheckResult(spec.Name, true, 0, "", "", TimeSpan.Zero, spec.Role)).ToList());
    }

    private sealed class TemporaryWorktreeGit : IGitClient
    {
        public Task<string> RevParseAsync(string refspec, string workingDirectory, CancellationToken ct) =>
            Task.FromResult("head");
        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorktreeInfo>>(Array.Empty<WorktreeInfo>());
        public Task<WorktreeRemoveResult> RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return Task.FromResult(new WorktreeRemoveResult(true, null));
        }
        public Task<IReadOnlyList<string>> GetBranchesNotMergedAsync(string pattern, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<WorktreeCreateResult> CreateWorktreeAsync(string path, string branch, string fromRef, string mainWorktreePath, CancellationToken ct)
        {
            Directory.CreateDirectory(path);
            return Task.FromResult(new WorktreeCreateResult(true, null, path));
        }
        public Task<string> HeadShaAsync(string worktreePath, CancellationToken ct) => Task.FromResult("head");
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
        public Task<int> RevListCountAsync(string range, string workingDirectory, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<string>> LogOnelineAsync(string range, int limit, string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> GetUntrackedFilesAsync(string workingDirectory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    [Fact]
    public async Task VerifyAsync_RejectsAGatingCheckWhoseCanaryStaysGreen()
    {
        var profile = new ProjectProfile(
            "example",
            "example-stack",
            "tool",
            "",
            "tool build",
            "tool test",
            "",
            new[]
            {
                new ProfileCheck(
                    "build",
                    "tool",
                    Array.Empty<string>(),
                    1,
                    new[] { new CanaryFile("src/__probe.txt", "broken") },
                    CheckRole.Gating)
            },
            Array.Empty<ProfileCheck>());
        var root = Path.Combine(Path.GetTempPath(), "profile-gate-verifier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var result = await new ProfileGateVerifier().VerifyAsync(
                profile,
                root,
                new TemporaryWorktreeGit(),
                new AlwaysGreenRunner(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("structurally vacuous", result.FailureReason);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
