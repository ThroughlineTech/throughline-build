using System.Text.Json;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

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
    public void CommandSurface_HasNoWorkerAgentDependency()
    {
        var parameterTypes = typeof(GateCommand)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType);

        Assert.DoesNotContain(typeof(IWorkerAgent), parameterTypes);
        Assert.DoesNotContain(typeof(IWorkerAgentFactory), parameterTypes);
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

        public override Task<IReadOnlyList<CheckResult>> RunAsync(
            IReadOnlyList<CheckSpec> specs,
            string workingDirectory,
            CancellationToken ct)
        {
            CallCount++;
            LastSpecs = OrderSetupFirst(specs);
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult<IReadOnlyList<CheckResult>>(
                LastSpecs.Select(_resultFactory).ToList());
        }
    }
}
