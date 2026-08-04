using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Scaffold;
using Xunit;
using WorkerBrief = ThroughlineBuild.Contracts.Models.Brief;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class ProfileCommandTests
{
    private const string ProfileJson = """
    {
      "language": "example",
      "framework": "example-stack",
      "package_manager": "tool",
      "build_command": "tool build",
      "test_command": "tool test",
      "review_checks": [
        {
          "name": "build",
          "executable": "tool",
          "arguments": ["build"],
          "timeout_minutes": 5,
          "role": "gating",
          "required_paths": ["project.file"],
          "canary": [{ "path": "src/__probe.txt", "content": "broken" }]
        }
      ]
    }
    """;

    private sealed class FakeWorker : IWorkerAgent
    {
        public string Name => "fake";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(
            WorkerBrief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct) =>
            Task.FromResult(new WorkerResult(
                Status.Ok,
                "derived",
                Array.Empty<string>(),
                null,
                new Dictionary<string, object>(),
                new Dictionary<string, string> { ["PROJECT_PROFILE"] = ProfileJson }));
    }

    private sealed class PassingVerifier : ProfileGateVerifier
    {
        public override Task<ProfileGateVerificationResult> VerifyAsync(
            ProjectProfile profile,
            string mainWorktreePath,
            IGitClient git,
            ThroughlineBuild.Verification.AutomatedChecksRunner runner,
            CancellationToken ct) =>
            Task.FromResult(ProfileGateVerificationResult.Ok());
    }

    [Fact]
    public async Task ApplyFromStdin_WritesProfileWithoutTicketingOrWorkerConfiguration()
    {
        var result = await RunInRepositoryAsync(
            "# profile apply consumes only valid TOML\n",
            ["profile", "apply", "-", "--json"],
            stdin: ProfileJson);

        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        Assert.Contains("executable = \"tool\"", result.ConfigText);
    }

    [Fact]
    public async Task ApplyRejectsMalformedJson()
    {
        var result = await RunInRepositoryAsync(
            "# valid but minimal\n",
            ["profile", "apply", "-", "--json"],
            stdin: "{ not JSON }");

        Assert.Equal(2, result.Exit);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("# valid but minimal\n", result.ConfigText);
    }

    [Fact]
    public async Task PromptEmitsWithoutRepositoryConfigurationOrCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "profile-prompt-tests", Guid.NewGuid().ToString("N"));
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.CreateDirectory(directory);
            Directory.SetCurrentDirectory(directory);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exit = await CliApplication.RunAsync(
                ["profile", "prompt", "--json"],
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            using var json = JsonDocument.Parse(stdout.ToString());
            var prompt = json.RootElement.GetProperty("data").GetProperty("prompt").GetString();
            Assert.Contains("Interrogate the repository itself", prompt);
            Assert.Contains("Return ONLY the JSON object", prompt);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ApplyIsIdempotentAndForceCanReplaceCustomizedChecks()
    {
        var initial = "# minimal config\n";
        var first = await RunInRepositoryAsync(initial, ["profile", "apply", "-"], stdin: ProfileJson,
            keepRepository: true);
        try
        {
            Assert.Equal(0, first.Exit);
            var second = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-"], ProfileJson);
            Assert.Equal(0, second.Exit);
            Assert.Equal(first.ConfigText, second.ConfigText);

            var customized = """
            [review]

            [[review.checks]]
            name = "custom"
            executable = "npm"
            arguments = ["test"]
            timeout_minutes = 5
            """;
            File.WriteAllText(Path.Combine(first.Repository!, ".build", "config.toml"), customized);

            var skipped = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-"], ProfileJson);
            Assert.Equal(0, skipped.Exit);
            Assert.Equal(customized, skipped.ConfigText);

            var forced = await RunAtRepositoryAsync(first.Repository!, ["profile", "apply", "-", "--force"], ProfileJson);
            Assert.Equal(0, forced.Exit);
            Assert.Contains("executable = \"tool\"", forced.ConfigText);
        }
        finally
        {
            TryDeleteDirectory(first.Repository!);
        }
    }

    [Fact]
    public async Task DeriveWritesEmptyConfigAndPreservesCustomizedChecksUnlessForced()
    {
        var directory = Path.Combine(Path.GetTempPath(), "profile-derive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".build"));
        var configPath = Path.Combine(directory, ".build", "config.toml");
        File.WriteAllText(configPath, "# empty config\n");

        try
        {
            var workers = Workers();
            var output = new StringWriter();
            var error = new StringWriter();

            var wrote = await ProfileCommand.ExecuteDeriveAsync(
                new ProfileInvocation(ProfileAction.Derive, false, null),
                json: false,
                configPath,
                directory,
                workers,
                (_, _) => new FakeWorker(),
                new PassingVerifier(),
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, wrote);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("executable = \"tool\"", File.ReadAllText(configPath));

            const string customized = """
            [review]

            [[review.checks]]
            name = "custom"
            executable = "npm"
            arguments = ["test"]
            timeout_minutes = 5
            """;
            File.WriteAllText(configPath, customized);
            bool workerCreated = false;

            var skipped = await ProfileCommand.ExecuteDeriveAsync(
                new ProfileInvocation(ProfileAction.Derive, false, null),
                json: false,
                configPath,
                directory,
                workers,
                (_, _) => { workerCreated = true; return new FakeWorker(); },
                new PassingVerifier(),
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, skipped);
            Assert.False(workerCreated);
            Assert.Equal(customized, File.ReadAllText(configPath));

            var forced = await ProfileCommand.ExecuteDeriveAsync(
                new ProfileInvocation(ProfileAction.Derive, true, null),
                json: false,
                configPath,
                directory,
                workers,
                (_, _) => new FakeWorker(),
                new PassingVerifier(),
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, forced);
            Assert.Contains("executable = \"tool\"", File.ReadAllText(configPath));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static WorkersConfig Workers() => new(
        "codex",
        5,
        new Dictionary<string, AgentConfig>(StringComparer.Ordinal)
        {
            ["codex"] = new AgentConfig("ignored", null, new Dictionary<WorkerSize, ModelTier>())
        },
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static async Task<(int Exit, string Stdout, string Stderr, string ConfigText, string? Repository)> RunInRepositoryAsync(
        string config,
        string[] args,
        string? stdin = null,
        bool keepRepository = false)
    {
        var repository = Path.Combine(Path.GetTempPath(), "profile-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repository, ".build"));
        await RunGitAsync(repository, "init");
        File.WriteAllText(Path.Combine(repository, ".build", "config.toml"), config);
        var result = await RunAtRepositoryAsync(repository, args, stdin);
        if (!keepRepository)
        {
            TryDeleteDirectory(repository);
            return (result.Exit, result.Stdout, result.Stderr, result.ConfigText, null);
        }

        return (result.Exit, result.Stdout, result.Stderr, result.ConfigText, repository);
    }

    private static async Task<(int Exit, string Stdout, string Stderr, string ConfigText, string? Repository)> RunAtRepositoryAsync(
        string repository,
        string[] args,
        string? stdin)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(repository);
            if (stdin is not null)
                Console.SetIn(new StringReader(stdin));
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));
            var config = File.ReadAllText(Path.Combine(repository, ".build", "config.toml"));
            return (exit, stdout.ToString(), stderr.ToString(), config, repository);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
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
        Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)} failed: {await stderr}; stdout: {await stdout}");
    }
}
