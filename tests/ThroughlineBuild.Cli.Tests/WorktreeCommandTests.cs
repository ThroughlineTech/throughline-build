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

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
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
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {await stderr}; stdout: {await stdout}");
    }
}
