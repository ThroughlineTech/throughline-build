using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class ProfileGateVerifierTests
{
    private sealed class MarkerInstaller : IInstallCommandRunner
    {
        public int CallCount { get; private set; }
        public string? WorkingDirectory { get; private set; }

        public Task<InstallCommandResult> RunAsync(
            string command,
            string workingDirectory,
            CancellationToken ct)
        {
            CallCount++;
            WorkingDirectory = workingDirectory;
            File.WriteAllText(Path.Combine(workingDirectory, ".installed"), command);
            return Task.FromResult(new InstallCommandResult(true, null));
        }
    }

    private sealed class InstallAwareRunner : AutomatedChecksRunner
    {
        public bool EveryRunSawInstall { get; private set; } = true;

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct) => Results(specs, workingDirectory);

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct,
            RequiredPathHandling requiredPathHandling) => Results(specs, workingDirectory);

        private Task<IReadOnlyList<CheckResult>> Results(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory)
        {
            EveryRunSawInstall &= File.Exists(Path.Combine(workingDirectory, ".installed"));
            return Task.FromResult<IReadOnlyList<CheckResult>>(specs.Select(spec =>
            {
                var canaryPresent = spec.Canary?.Any(canary =>
                    File.Exists(Path.Combine(workingDirectory, canary.Path))) == true;
                return new CheckResult(
                    spec.Name,
                    !canaryPresent,
                    canaryPresent ? 1 : 0,
                    "",
                    "",
                    TimeSpan.Zero,
                    spec.Role);
            }).ToList());
        }
    }

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

    [Fact]
    public async Task VerifyAsync_InstallsOnceBeforeRunningChecksInThrowawayWorktree()
    {
        var profile = new ProjectProfile(
            "example",
            "example-stack",
            "tool",
            "tool install",
            "tool build",
            "tool test",
            "",
            [
                new ProfileCheck(
                    "build",
                    "tool",
                    ["build"],
                    1,
                    [new CanaryFile("src/probe.txt", "broken")],
                    CheckRole.Gating)
            ],
            Array.Empty<ProfileCheck>());
        var root = Path.Combine(
            Path.GetTempPath(),
            "profile-gate-verifier-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var installer = new MarkerInstaller();
        var runner = new InstallAwareRunner();

        try
        {
            var result = await new ProfileGateVerifier(installer).VerifyAsync(
                profile,
                root,
                new TemporaryWorktreeGit(),
                runner,
                CancellationToken.None);

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal(1, installer.CallCount);
            Assert.NotNull(installer.WorkingDirectory);
            Assert.True(runner.EveryRunSawInstall);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_RejectsInstallCommandDuplicatedAsSetupBeforeCreatingWorktree()
    {
        var profile = new ProjectProfile(
            "example",
            "example-stack",
            "npm",
            "npm install",
            "npm run build",
            "npm test",
            "",
            [
                new ProfileCheck(
                    "install",
                    "npm",
                    ["install"],
                    1,
                    null,
                    CheckRole.Setup),
                new ProfileCheck(
                    "test",
                    "npm",
                    ["test"],
                    1,
                    [new CanaryFile("test/probe.test.js", "broken")],
                    CheckRole.Gating)
            ],
            Array.Empty<ProfileCheck>());
        var root = Path.Combine(
            Path.GetTempPath(),
            "profile-gate-verifier-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var installer = new MarkerInstaller();

        try
        {
            var result = await new ProfileGateVerifier(installer).VerifyAsync(
                profile,
                root,
                new TemporaryWorktreeGit(),
                new InstallAwareRunner(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("duplicates install_command", result.FailureReason);
            Assert.Equal(0, installer.CallCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_RejectsAGatingCheckWithNoCanary()
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
                    null,
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
            Assert.Contains("has no canary", result.FailureReason);
            Assert.Contains("cannot prove it is non-vacuous", result.FailureReason);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
