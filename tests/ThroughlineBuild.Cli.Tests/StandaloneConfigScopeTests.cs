using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class StandaloneConfigScopeTests
{
    [Fact]
    public async Task GateRequireChecksEmptyConfigSkipsTicketingWorkersEvents()
    {
        var (exit, stdout, stderr) = await RunInRepoAsync(
            "# no ticketing, workers, or events sections\n",
            ["gate", "--require-checks", "--json"]);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("passed").GetBoolean());
        Assert.False(data.GetProperty("checksConfigured").GetBoolean());
    }

    [Fact]
    public async Task GateRoleMismatchWithConfiguredChecksReportsConfiguredWithoutTicketingWorkersEvents()
    {
        var (exit, stdout, stderr) = await RunInRepoAsync(
            """
            [review]

            [[review.checks]]
            name = "build"
            executable = "dotnet"
            role = "gating"
            """,
            ["gate", "--role", "advisory", "--require-checks", "--json"]);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("checksConfigured").GetBoolean());
        Assert.False(data.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task WavesUsesScopedConfigWithoutTicketingWorkersEvents()
    {
        var (exit, stdout, stderr) = await RunInRepoAsync(
            """
            [waves]
            cap = 4
            """,
            ["waves", "--input", "-", "--json"],
            stdin: """[{"id":"TLB-1","files":["README.md"],"deps":[]}]""");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(4, json.RootElement.GetProperty("data").GetProperty("cap").GetInt32());
    }

    [Fact]
    public async Task WorktreeListUsesScopedConfigWithoutTicketingWorkersEvents()
    {
        var (exit, stdout, stderr) = await RunInRepoAsync(
            """
            [worktree]
            root = ".leases"

            [project]
            install_command = ""
            """,
            ["worktree", "list", "--json"]);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CandidateStatusUsesScopedConfigWithoutTicketingWorkersEvents()
    {
        var (exit, stdout, stderr) = await RunInRepoAsync(
            "# no ticketing, workers, or events sections\n",
            ["candidate", "status", "--ticket", "TLB-600", "--base", "HEAD", "--json"],
            createInitialCommit: true);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("TLB-600", json.RootElement.GetProperty("data").GetProperty("ticket").GetString());
    }

    [Fact]
    public async Task TicketingCommandStillRejectsIncompleteConfig()
    {
        var (exit, stdout, _) = await RunInRepoAsync(
            "# no ticketing, workers, or events sections\n",
            ["list", "--json"]);

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            CliErrorCodes.ConfigError,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WavesStillRejectsMalformedConsumedSection()
    {
        var inputName = "waves.json";
        var (exit, stdout, _) = await RunInRepoAsync(
            """
            [waves]
            cap = 0
            """,
            ["waves", "--input", inputName, "--json"],
            files: new Dictionary<string, string>
            {
                [inputName] = """[{"id":"TLB-1","files":["README.md"],"deps":[]}]"""
            });

        Assert.Equal(2, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            CliErrorCodes.ConfigError,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunInRepoAsync(
        string config,
        string[] args,
        IReadOnlyDictionary<string, string>? files = null,
        string? stdin = null,
        bool createInitialCommit = false)
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "standalone-config-scope-tests",
            Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.CreateDirectory(Path.Combine(repository, ".build"));
            await RunGitAsync(repository, "init");
            if (createInitialCommit)
            {
                await RunGitAsync(repository, "config", "user.email", "test@test.com");
                await RunGitAsync(repository, "config", "user.name", "Test");
                await RunGitAsync(repository, "commit", "--allow-empty", "-m", "initial");
            }
            File.WriteAllText(Path.Combine(repository, ".build", "config.toml"), config);
            if (files is not null)
            {
                foreach (var file in files)
                    File.WriteAllText(Path.Combine(repository, file.Key), file.Value);
            }

            Directory.SetCurrentDirectory(repository);
            if (stdin is not null)
                Console.SetIn(new StringReader(stdin));
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
            TryDeleteDirectory(repository);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
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
        var stdoutText = await stdout;
        var stderrText = await stderr;
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {stderrText}; stdout: {stdoutText}");
    }
}
