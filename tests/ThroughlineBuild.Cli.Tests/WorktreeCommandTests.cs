using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class WorktreeCommandTests
{
    [Fact]
    public async Task ListJsonEmitsStandardEnvelopeWithoutUsingWorkerOrTicketing()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "unmanifested"));
        try
        {
            var manager = CreateManager(root);
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "list"],
                json: true,
                manager,
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("data").GetProperty("leases").EnumerateArray());
            Assert.Single(json.RootElement.GetProperty("data")
                .GetProperty("unmanifestedDirectories").EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeaseMissingTicketEmitsUsageEnvelopeAndExitTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-command-tests", Guid.NewGuid().ToString("N"));
        var output = new StringWriter();

        var exit = await WorktreeCommand.ExecuteAsync(
            ["worktree", "lease", "--slug", "missing-ticket"],
            json: true,
            CreateManager(root),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "usage",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CliWorktreeListDoesNotResolveMissingTicketingSecret()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(repository, ".build"));

        try
        {
            await RunGitAsync(repository, "init");
            File.WriteAllText(
                Path.Combine(repository, ".build", "config.toml"),
                """
                [ticketing]
                backend = "plane"
                plane_base_url = "https://api.plane.test"
                plane_workspace_slug = "workspace"
                plane_project_id = "project"
                plane_api_token_env = "TLB586_INTENTIONALLY_MISSING_WORKTREE_TOKEN"

                [workers]
                default_agent = "codex"

                [workers.codex]
                executable = "worker-must-not-run"

                [workers.codex.sizes]
                small = { model = "test" }
                medium = { model = "test" }
                large = { model = "test" }

                [events]
                log_directory = ".build/events"
                """);
            Directory.SetCurrentDirectory(repository);

            var exit = await CliApplication.RunAsync(
                ["worktree", "list"],
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));

            Assert.Equal(0, exit);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public async Task TeardownNullManifestFieldEmitsInvalidManifestEnvelopeAndExitEight()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        Directory.CreateDirectory(repository);
        WorktreeLeaseResult? leased = null;

        try
        {
            await RunGitAsync(repository, "init", "-b", "main");
            await RunGitAsync(repository, "config", "user.email", "test@test.com");
            await RunGitAsync(repository, "config", "user.name", "Test");
            await RunGitAsync(repository, "commit", "--allow-empty", "-m", "initial");
            var manager = new WorktreeLeaseManager(
                new ProcessGitClient(repository),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    repository,
                    repository,
                    root,
                    Array.Empty<string>(),
                    string.Empty));
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-586"), CancellationToken.None);
            Assert.True(leased.Success);
            var manifestPath = Path.Combine(
                leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace(
                    $"\"baseSha\": \"{leased.Manifest.BaseSha}\"",
                    "\"baseSha\": null",
                    StringComparison.Ordinal));
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(8, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                WorktreeLeaseManager.InvalidManifestError,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
        }
        finally
        {
            if (leased?.Manifest is { } manifest)
            {
                await RunGitAsync(
                    repository, "worktree", "remove", "--force", manifest.WorktreePath);
                await RunGitAsync(repository, "branch", "-D", manifest.Branch);
            }
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Git can briefly retain Windows handles after linked-worktree cleanup.
            }
        }
    }

    [Fact]
    public async Task TeardownDefaultSucceedsForCleanIntegratedLeaseWithManifestAndSeed()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            File.WriteAllText(Path.Combine(repository, ".dev.vars"), "SECRET=value");
            var manager = new WorktreeLeaseManager(
                new ProcessGitClient(repository),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    repository,
                    repository,
                    root,
                    [".dev.vars"],
                    string.Empty));
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "clean", RequiredSeed: ".dev.vars"),
                CancellationToken.None);
            Assert.True(leased.Success);
            Assert.True(File.Exists(Path.Combine(
                leased.Manifest!.WorktreePath, WorktreeLeaseConstants.ManifestFileName)));
            Assert.True(File.Exists(Path.Combine(leased.Manifest.WorktreePath, ".dev.vars")));
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--ticket", "TLB-592"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Equal(
                string.Empty,
                (await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch)).Trim());
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownDefaultCorruptIndexFailsClosedAndLeavesWorktreeAndBranch()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "base");
            await RunGitAsync(repository, "add", "tracked.txt");
            await RunGitAsync(repository, "commit", "-m", "tracked");
            var manager = new WorktreeLeaseManager(
                new ProcessGitClient(repository),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    repository,
                    repository,
                    root,
                    Array.Empty<string>(),
                    string.Empty));
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "corrupt-index"),
                CancellationToken.None);
            Assert.True(leased.Success);
            File.WriteAllText(Path.Combine(leased.Manifest!.WorktreePath, "tracked.txt"), "modified");
            var gitDir = ReadLinkedWorktreeGitDirectory(leased.Manifest.WorktreePath);
            File.WriteAllText(Path.Combine(gitDir, "index"), "not a git index");
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            var error = json.RootElement.GetProperty("error");
            Assert.Equal(WorktreeLeaseManager.FailureError, error.GetProperty("code").GetString());
            Assert.Contains("git status --porcelain", error.GetProperty("message").GetString());
            Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Contains(
                leased.Manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch));
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownRequireMergedIntoSucceedsWhenLeaseBranchIsInNamedTarget()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = CreateRealManager(repository, root);
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "merged-main"),
                CancellationToken.None);
            Assert.True(leased.Success);
            await CommitInLeaseAsync(leased.Manifest!, "branch-only.txt", "branch work");
            await RunGitAsync(repository, "merge", "--ff-only", leased.Manifest!.Branch);
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath, "--require-merged-into", "main"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Equal(
                string.Empty,
                (await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch)).Trim());
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownRequireMergedIntoRefusesUnmergedBranchBeforeWorktreeRemoval()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = CreateRealManager(repository, root);
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "unmerged-target"),
                CancellationToken.None);
            Assert.True(leased.Success);
            var manifest = leased.Manifest!;
            await CommitInLeaseAsync(manifest, "branch-only.txt", "branch work");
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", manifest.WorktreePath, "--require-merged-into", "main"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(
                "not proven merged into 'main'",
                json.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.True(Directory.Exists(manifest.WorktreePath));
            Assert.Contains(
                manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--list", manifest.Branch));
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownRequireMergedIntoRefusesBranchMergedIntoDifferentRefBeforeWorktreeRemoval()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = CreateRealManager(repository, root);
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "merged-elsewhere"),
                CancellationToken.None);
            Assert.True(leased.Success);
            await CommitInLeaseAsync(leased.Manifest!, "branch-only.txt", "branch work");
            await RunGitAsync(repository, "branch", "integration", leased.Manifest!.Branch);
            Assert.Contains(
                leased.Manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--contains", "integration", "--list", leased.Manifest.Branch));
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath, "--require-merged-into", "main"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Contains(
                leased.Manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch));
        }
        finally
        {
            try { await RunGitAsync(repository, "branch", "-D", "integration"); } catch { }
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownRequireMergedIntoRefusesWhenSideBranchHeadContainsLeaseButNamedTargetDoesNot()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = CreateRealManager(repository, root);
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "side-head"),
                CancellationToken.None);
            Assert.True(leased.Success);
            await CommitInLeaseAsync(leased.Manifest!, "branch-only.txt", "branch work");
            await RunGitAsync(repository, "switch", "-c", "side-head-main");
            await RunGitAsync(repository, "merge", "--ff-only", leased.Manifest!.Branch);
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath, "--require-merged-into", "main"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exit);
            Assert.True(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Contains(
                leased.Manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch));
        }
        finally
        {
            try { await RunGitAsync(repository, "switch", "main"); } catch { }
            try { await RunGitAsync(repository, "branch", "-D", "side-head-main"); } catch { }
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownForceSkipsRequireMergedIntoProof()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = CreateRealManager(repository, root);
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "force-target"),
                CancellationToken.None);
            Assert.True(leased.Success);
            var manifest = leased.Manifest!;
            await CommitInLeaseAsync(manifest, "branch-only.txt", "branch work");
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", manifest.WorktreePath, "--require-merged-into", "main", "--force"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.False(Directory.Exists(manifest.WorktreePath));
            Assert.Equal(
                string.Empty,
                (await RunGitOutputAsync(repository, "branch", "--list", manifest.Branch)).Trim());
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownRequireMergedIntoBlankValueIsUsageError()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-command-tests", Guid.NewGuid().ToString("N"));
        var output = new StringWriter();

        var exit = await WorktreeCommand.ExecuteAsync(
            ["worktree", "teardown", "--dir", root, "--require-merged-into", ""],
            json: true,
            CreateManager(root),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "usage",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(
            "non-empty ref",
            json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TeardownDefaultPreservesUnmergedBranchAfterRemovingWorktree()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = new WorktreeLeaseManager(
                new ProcessGitClient(repository),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    repository,
                    repository,
                    root,
                    Array.Empty<string>(),
                    string.Empty));
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "unmerged"),
                CancellationToken.None);
            Assert.True(leased.Success);
            File.WriteAllText(Path.Combine(leased.Manifest!.WorktreePath, "branch-only.txt"), "keep");
            await RunGitAsync(leased.Manifest.WorktreePath, "add", "branch-only.txt");
            await RunGitAsync(leased.Manifest.WorktreePath, "commit", "-m", "branch work");
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(1, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(
                "worktree removed but branch deletion failed",
                json.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Contains(
                leased.Manifest.Branch,
                await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch));
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    [Fact]
    public async Task TeardownForceAcceptsBareFlag()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "worktree-command-tests",
            Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(temp, "repo");
        var root = Path.Combine(temp, "leases");
        WorktreeLeaseResult? leased = null;

        try
        {
            await InitializeRepositoryAsync(repository);
            var manager = new WorktreeLeaseManager(
                new ProcessGitClient(repository),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    repository,
                    repository,
                    root,
                    Array.Empty<string>(),
                    string.Empty));
            leased = await manager.LeaseAsync(
                new WorktreeLeaseRequest("TLB-592", "force"),
                CancellationToken.None);
            Assert.True(leased.Success);
            File.WriteAllText(Path.Combine(leased.Manifest!.WorktreePath, "stray.txt"), "discard");
            var output = new StringWriter();

            var exit = await WorktreeCommand.ExecuteAsync(
                ["worktree", "teardown", "--dir", leased.Manifest.WorktreePath, "--force"],
                json: true,
                manager,
                output,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.False(Directory.Exists(leased.Manifest.WorktreePath));
            Assert.Equal(
                string.Empty,
                (await RunGitOutputAsync(repository, "branch", "--list", leased.Manifest.Branch)).Trim());
        }
        finally
        {
            await CleanupLeaseAsync(repository, leased?.Manifest);
            TryDeleteDirectory(temp);
        }
    }

    private static WorktreeLeaseManager CreateManager(string root)
    {
        var repository = Directory.GetCurrentDirectory();
        return new WorktreeLeaseManager(
            new ProcessGitClient(repository),
            new ProcessInstallCommandRunner(),
            new WorktreeLeaseOptions(
                repository,
                repository,
                root,
                Array.Empty<string>(),
                string.Empty));
    }

    private static WorktreeLeaseManager CreateRealManager(string repository, string root) =>
        new(
            new ProcessGitClient(repository),
            new ProcessInstallCommandRunner(),
            new WorktreeLeaseOptions(
                repository,
                repository,
                root,
                Array.Empty<string>(),
                string.Empty));

    private static async Task CommitInLeaseAsync(
        WorktreeLeaseManifest manifest,
        string relativePath,
        string message)
    {
        File.WriteAllText(Path.Combine(manifest.WorktreePath, relativePath), message);
        await RunGitAsync(manifest.WorktreePath, "add", relativePath);
        await RunGitAsync(manifest.WorktreePath, "commit", "-m", message);
    }

    private static string ReadLinkedWorktreeGitDirectory(string worktreePath)
    {
        var gitFile = Path.Combine(worktreePath, ".git");
        var content = File.ReadAllText(gitFile).Trim();
        const string prefix = "gitdir:";
        Assert.StartsWith(prefix, content);
        var raw = content.Substring(prefix.Length).Trim();
        return Path.GetFullPath(Path.IsPathRooted(raw)
            ? raw
            : Path.Combine(worktreePath, raw));
    }

    private static async Task InitializeRepositoryAsync(string repository)
    {
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.email", "test@test.com");
        await RunGitAsync(repository, "config", "user.name", "Test");
        await RunGitAsync(repository, "commit", "--allow-empty", "-m", "initial");
    }

    private static async Task CleanupLeaseAsync(string repository, WorktreeLeaseManifest? manifest)
    {
        if (manifest is null || !Directory.Exists(repository))
            return;

        if (Directory.Exists(manifest.WorktreePath))
        {
            try { await RunGitAsync(repository, "worktree", "remove", "--force", manifest.WorktreePath); }
            catch { }
        }

        try { await RunGitAsync(repository, "branch", "-D", manifest.Branch); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Git can briefly retain Windows handles after linked-worktree cleanup.
        }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunGitCaptureAsync(workingDirectory, args);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {result.Stderr}; stdout: {result.Stdout}");
    }

    private static async Task<string> RunGitOutputAsync(string workingDirectory, params string[] args)
    {
        var result = await RunGitCaptureAsync(workingDirectory, args);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {result.Stderr}; stdout: {result.Stdout}");
        return result.Stdout;
    }

    private static async Task<GitProcessResult> RunGitCaptureAsync(
        string workingDirectory,
        params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record GitProcessResult(int ExitCode, string Stdout, string Stderr);
}
