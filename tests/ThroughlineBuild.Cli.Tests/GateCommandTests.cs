using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class GateCommandTests
{
    [Fact]
    public async Task NoChecksConfigured_ReportsNonFailureWithoutRunningAnything()
    {
        var runner = new RecordingRunner();
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate"],
            json: false,
            Array.Empty<CheckSpec>(),
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(0, runner.CallCount);
        Assert.Contains("no checks configured", output.ToString());
    }

    [Fact]
    public async Task RequireChecks_NoChecksConfigured_ExitsOneAndReportsFailedGate()
    {
        var runner = new RecordingRunner();
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--require-checks"],
            json: true,
            Array.Empty<CheckSpec>(),
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(0, runner.CallCount);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("passed").GetBoolean());
        Assert.Equal("no checks configured or selected", data.GetProperty("message").GetString());
        Assert.Empty(data.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task RequireChecks_RoleSelectsNoChecks_ExitsOneWithoutRunningAnything()
    {
        var runner = new RecordingRunner();
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--role", "advisory", "--require-checks"],
            json: true,
            [Spec("test", CheckRole.Gating)],
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(0, runner.CallCount);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("checksConfigured").GetBoolean());
        Assert.False(data.GetProperty("passed").GetBoolean());
        Assert.Equal("no checks configured or selected", data.GetProperty("message").GetString());
        Assert.Empty(data.GetProperty("checks").EnumerateArray());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task RequireChecks_SelectedChecksUseNormalPassFailBehavior(bool checkPassed, int expectedExit)
    {
        var runner = new RecordingRunner(resultFactory: spec =>
            Result(spec, passed: checkPassed, exitCode: checkPassed ? 0 : 1, stderr: checkPassed ? "" : "tests failed"));
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--role", "gating", "--require-checks"],
            json: true,
            [Spec("test", CheckRole.Gating)],
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(expectedExit, exit);
        Assert.Equal(1, runner.CallCount);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(checkPassed, data.GetProperty("passed").GetBoolean());
        Assert.Equal(checkPassed ? "gate passed" : "gate failed", data.GetProperty("message").GetString());
        Assert.Single(data.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task AdvisoryFailure_IsReportedButDoesNotFailGate()
    {
        var runner = new RecordingRunner(resultFactory: spec =>
            Result(spec, passed: false, exitCode: 7, stdout: "lint warning"));
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--role", "advisory"],
            json: true,
            [Spec("lint", CheckRole.Advisory)],
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("passed").GetBoolean());
        var check = data.GetProperty("checks").EnumerateArray().Single();
        Assert.Equal("failed", check.GetProperty("status").GetString());
        Assert.Equal(7, check.GetProperty("exitCode").GetInt32());
        Assert.Equal("lint warning", check.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task GatingFailure_ExitsOne_AndJsonCarriesTypedEvidence()
    {
        var runner = new RecordingRunner(resultFactory: spec =>
            Result(spec, passed: false, exitCode: 1, stderr: "tests failed"));
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--ticket", "TLB-583", "--role", "gating"],
            json: true,
            [Spec("test", CheckRole.Gating)],
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var data = root.GetProperty("data");
        Assert.Equal("TLB-583", data.GetProperty("ticket").GetString());
        Assert.False(data.GetProperty("passed").GetBoolean());
        var check = data.GetProperty("checks").EnumerateArray().Single();
        Assert.Equal("test", check.GetProperty("name").GetString());
        Assert.Equal("gating", check.GetProperty("role").GetString());
        Assert.Equal("failed", check.GetProperty("status").GetString());
        Assert.Equal("tests failed", check.GetProperty("stderr").GetString());
        Assert.True(check.TryGetProperty("durationMilliseconds", out _));
    }

    [Fact]
    public async Task RoleFilter_IncludesSetupExactlyOnceBeforeSelectedChecks()
    {
        var runner = new RecordingRunner();
        var checks = new[]
        {
            Spec("lint", CheckRole.Advisory),
            Spec("prepare", CheckRole.Setup),
            Spec("test", CheckRole.Gating),
        };

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--role", "gating"],
            json: false,
            checks,
            Directory.GetCurrentDirectory(),
            runner,
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["prepare", "test"], runner.LastSpecs.Select(s => s.Name));
        Assert.Single(runner.LastSpecs, s => s.Role == CheckRole.Setup);
        Assert.Equal(
            AutomatedChecksRunner.RequiredPathHandling.Inconclusive,
            runner.LastRequiredPathHandling);
    }

    [Fact]
    public async Task SetupFailure_FailsGate()
    {
        var runner = new RecordingRunner(resultFactory: spec =>
            spec.Role == CheckRole.Setup
                ? Result(spec, passed: false, exitCode: 9, stderr: "setup failed")
                : Result(spec));

        var exit = await GateCommand.ExecuteAsync(
            ["gate", "--role", "all"],
            json: false,
            [Spec("test", CheckRole.Gating), Spec("prepare", CheckRole.Setup)],
            Directory.GetCurrentDirectory(),
            runner,
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Single(runner.LastSpecs, spec => spec.Role == CheckRole.Setup);
    }

    [Fact]
    public async Task InconclusiveGatingResult_IsDistinctAndFailsGate()
    {
        var runner = new RecordingRunner(resultFactory: spec =>
            new CheckResult(
                spec.Name,
                false,
                -1,
                "",
                "required path absent",
                TimeSpan.Zero,
                spec.Role,
                CommandLine: spec.Executable,
                Inconclusive: true,
                MissingRequiredPaths: ["src"]));
        var output = new StringWriter();

        var exit = await GateCommand.ExecuteAsync(
            ["gate"],
            json: true,
            [Spec("build", CheckRole.Gating)],
            Directory.GetCurrentDirectory(),
            runner,
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var check = json.RootElement.GetProperty("data").GetProperty("checks").EnumerateArray().Single();
        Assert.Equal("inconclusive", check.GetProperty("status").GetString());
        Assert.Equal("src", check.GetProperty("missingRequiredPaths").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task MissingRequiredPath_GateOptInDoesNotExecuteCommand_AndReturnsInconclusive()
    {
        var missing = $"missing-{Guid.NewGuid():N}";
        var output = new StringWriter();
        var check = new CheckSpec(
            "requires-input",
            "this-command-must-not-run",
            Array.Empty<string>(),
            TimeSpan.FromSeconds(5),
            CheckRole.Gating,
            RequiredPaths: [missing]);

        var exit = await GateCommand.ExecuteAsync(
            ["gate"],
            json: true,
            [check],
            Directory.GetCurrentDirectory(),
            new AutomatedChecksRunner(),
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var evidence = json.RootElement
            .GetProperty("data")
            .GetProperty("checks")
            .EnumerateArray()
            .Single();
        Assert.Equal("inconclusive", evidence.GetProperty("status").GetString());
        Assert.Equal(-1, evidence.GetProperty("exitCode").GetInt32());
        Assert.Equal(missing, evidence.GetProperty("missingRequiredPaths").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task UsesInvocationDirectory_NotPrimaryWorktree()
    {
        var leasedWorktree = Path.Combine(Path.GetTempPath(), "gate-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leasedWorktree);
        try
        {
            var runner = new RecordingRunner();

            var exit = await GateCommand.ExecuteAsync(
                ["gate"],
                json: false,
                [Spec("test", CheckRole.Gating)],
                leasedWorktree,
                runner,
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Equal(Path.GetFullPath(leasedWorktree), Path.GetFullPath(runner.LastWorkingDirectory!));
        }
        finally
        {
            Directory.Delete(leasedWorktree);
        }
    }

    [Fact]
    public async Task CliGate_DoesNotConstructOrInvokeWorkerAgent()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "gate-command-tests",
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
                plane_api_token_env = "TLB586_INTENTIONALLY_MISSING_GATE_TOKEN"

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

            var worker = new RecordingWorkerAgent();
            var constructionCount = 0;
            Directory.SetCurrentDirectory(repository);

            var exit = await CliApplication.RunAsync(
                ["gate"],
                (_, _) =>
                {
                    constructionCount++;
                    return worker;
                });

            Assert.Equal(0, exit);
            Assert.Equal(0, constructionCount);
            Assert.Equal(0, worker.InvocationCount);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(repository, recursive: true);
        }
    }

    private static CheckSpec Spec(string name, CheckRole role) =>
        new(name, "configured-command", Array.Empty<string>(), TimeSpan.FromSeconds(5), role);

    private static CheckResult Result(
        CheckSpec spec,
        bool passed = true,
        int exitCode = 0,
        string stdout = "",
        string stderr = "") =>
        new(spec.Name, passed, exitCode, stdout, stderr, TimeSpan.FromMilliseconds(12), spec.Role);

    private sealed class RecordingRunner : AutomatedChecksRunner
    {
        private readonly Func<CheckSpec, CheckResult> _resultFactory;

        public RecordingRunner(Func<CheckSpec, CheckResult>? resultFactory = null)
        {
            _resultFactory = resultFactory ?? (spec => Result(spec));
        }

        public int CallCount { get; private set; }
        public IReadOnlyList<CheckSpec> LastSpecs { get; private set; } = Array.Empty<CheckSpec>();
        public string? LastWorkingDirectory { get; private set; }
        public AutomatedChecksRunner.RequiredPathHandling? LastRequiredPathHandling { get; private set; }

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct,
            RequiredPathHandling requiredPathHandling)
        {
            CallCount++;
            LastSpecs = OrderSetupFirst(specs);
            LastWorkingDirectory = workingDirectory;
            LastRequiredPathHandling = requiredPathHandling;
            return Task.FromResult<IReadOnlyList<CheckResult>>(
                LastSpecs.Select(_resultFactory).ToList());
        }
    }

    private sealed class RecordingWorkerAgent : IWorkerAgent
    {
        public string Name => "recording";
        public IWorkerProgressDigester? Digester => null;
        public int InvocationCount { get; private set; }

        public Task<WorkerResult> ExecuteAsync(
            Brief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(new WorkerResult(
                Status.Ok,
                "unexpected invocation",
                Array.Empty<string>(),
                null,
                new Dictionary<string, object>()));
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
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {await stderr}; stdout: {await stdout}");
    }
}
