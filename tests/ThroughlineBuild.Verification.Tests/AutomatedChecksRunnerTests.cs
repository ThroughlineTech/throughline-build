using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class AutomatedChecksRunnerTests
{
    // Helper: build a CheckSpec with a generous default timeout
    private static CheckSpec Spec(string name, string exe, IReadOnlyList<string> args,
        TimeSpan? timeout = null)
        => new CheckSpec(name, exe, args, timeout ?? TimeSpan.FromSeconds(30));

    private static CheckSpec SpecRole(string name, CheckRole role)
        => new CheckSpec(name, "noop", Array.Empty<string>(), TimeSpan.FromSeconds(5), role);

    // Cross-platform "sleep for ~N seconds" command for synthesizing long-running processes.
    // Windows lacks `sleep`; macOS/Linux lack `cmd`/`ping -n`.
    private static (string exe, string[] args) SleepCmd(int seconds)
        => OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", $"ping -n {seconds + 1} 127.0.0.1 >NUL" })
            : ("sh", new[] { "-c", $"sleep {seconds}" });

    // --- Test (a): All-pass ---
    // Two specs that both exit 0: "dotnet --version" and "dotnet --version" again.
    // Both should have Passed=true, ExitCode=0, Elapsed > Zero.
    [Fact]
    public async Task AllPass_BothResultsPassedWithElapsedGreaterThanZero()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            Spec("version1", "dotnet", new[] { "--version" }),
            Spec("version2", "dotnet", new[] { "--version" })
        };

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(2, results.Count);

        Assert.True(results[0].Passed, $"spec1 should pass; stderr={results[0].StderrTail}");
        Assert.Equal(0, results[0].ExitCode);
        Assert.True(results[0].Elapsed > TimeSpan.Zero, "spec1 elapsed should be > Zero");

        Assert.True(results[1].Passed, $"spec2 should pass; stderr={results[1].StderrTail}");
        Assert.Equal(0, results[1].ExitCode);
        Assert.True(results[1].Elapsed > TimeSpan.Zero, "spec2 elapsed should be > Zero");
    }

    // --- TLB-523: setup-role specs are prerequisites that must run before the rest ---
    [Fact]
    public void OrderSetupFirst_MovesSetupSpecsAhead_StableWithinGroups()
    {
        var specs = new[]
        {
            SpecRole("build", CheckRole.Gating),
            SpecRole("gen-a", CheckRole.Setup),
            SpecRole("lint", CheckRole.Advisory),
            SpecRole("gen-b", CheckRole.Setup),
        };

        var ordered = AutomatedChecksRunner.OrderSetupFirst(specs);

        // Setup steps first (in their original relative order), then the rest (in theirs).
        Assert.Equal(new[] { "gen-a", "gen-b", "build", "lint" }, ordered.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void OrderSetupFirst_NoSetupSpecs_ReturnsInputUnchanged()
    {
        var specs = new[]
        {
            SpecRole("build", CheckRole.Gating),
            SpecRole("lint", CheckRole.Advisory),
        };

        var ordered = AutomatedChecksRunner.OrderSetupFirst(specs);

        Assert.Same(specs, ordered); // nothing to reorder -> input returned untouched
    }

    // --- Test (b): One-failure-default-mode (run-all) ---
    // spec1: "cmd /c exit 1" -> fails
    // spec2: "dotnet --version" -> succeeds
    // stopOnFirstFailure=false: both specs should run; spec1.Passed=false, spec2.Passed=true
    [Fact]
    public async Task OneFailure_DefaultMode_RunsAllSpecs()
    {
        var runner = new AutomatedChecksRunner(stopOnFirstFailure: false);
        var specs = new[]
        {
            // cmd /c exit 1 produces exit code 1
            Spec("fail1", "cmd", new[] { "/c", "exit 1" }),
            // dotnet --version produces exit code 0 and prints version to stdout
            Spec("version", "dotnet", new[] { "--version" })
        };

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(2, results.Count);

        Assert.False(results[0].Passed, "spec1 should fail");
        Assert.NotEqual(0, results[0].ExitCode);

        Assert.True(results[1].Passed, $"spec2 should pass; stderr={results[1].StderrTail}");
        Assert.Equal(0, results[1].ExitCode);
        // Proof that spec2 actually executed: its stdout has version text and elapsed > Zero
        Assert.True(results[1].Elapsed > TimeSpan.Zero, "spec2 should have elapsed > Zero, proving it ran");
        Assert.False(string.IsNullOrWhiteSpace(results[1].StdoutTail), "spec2 stdout should contain version");
    }

    // --- Test (c): One-failure-stop-mode ---
    // spec1: "cmd /c exit 1" -> fails
    // spec2: "dotnet --version" -> would succeed if run
    // stopOnFirstFailure=true: spec2 should be not-run (ExitCode=-1, Passed=false, Elapsed=Zero)
    [Fact]
    public async Task OneFailure_StopMode_SecondSpecIsNotRun()
    {
        var runner = new AutomatedChecksRunner(stopOnFirstFailure: true);
        var specs = new[]
        {
            Spec("fail1", "cmd", new[] { "/c", "exit 1" }),
            Spec("version", "dotnet", new[] { "--version" })
        };

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(2, results.Count);

        Assert.False(results[0].Passed, "spec1 should fail");

        Assert.False(results[1].Passed, "spec2 should be reported as not-run (Passed=false)");
        Assert.Equal(-1, results[1].ExitCode);
        Assert.Equal(TimeSpan.Zero, results[1].Elapsed);
    }

    // --- Test (d): Timeout ---
    // spec: sleep ~5s (cross-platform) but timeout=200ms
    // Expected: Passed=false, StderrTail contains "timeout after"
    [Fact]
    public async Task Timeout_MarksSpecAsFailed_AndAppendsTimeoutMessage()
    {
        var runner = new AutomatedChecksRunner();
        var (exe, args) = SleepCmd(5);
        var specs = new[]
        {
            new CheckSpec("slow", exe, args, TimeSpan.FromMilliseconds(200))
        };

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed, "timed-out spec should be Passed=false");
        Assert.Contains("timeout after", results[0].StderrTail, StringComparison.OrdinalIgnoreCase);
    }

    // --- CommandLine carriage ---
    // Every executed result records the exact launchable command line so rework briefs can
    // instruct the worker to re-run the failing check verbatim and confirm exit 0.

    [Fact]
    public void FormatCommandLine_JoinsExecutableAndArguments()
    {
        var spec = Spec("lint", "swiftlint", new[] { "--strict", "--no-cache" });
        Assert.Equal("swiftlint --strict --no-cache", AutomatedChecksRunner.FormatCommandLine(spec));
    }

    [Fact]
    public void FormatCommandLine_NoArguments_ExecutableOnly()
    {
        var spec = Spec("fmt", "gofmt", Array.Empty<string>());
        Assert.Equal("gofmt", AutomatedChecksRunner.FormatCommandLine(spec));
    }

    [Fact]
    public async Task RunNamed_ExecutedCheck_CarriesCommandLine()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[] { Spec("version", "dotnet", new[] { "--version" }) };

        var result = await runner.RunNamedAsync("version", specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal("dotnet --version", result.CommandLine);
    }

    // --- Tests (f-h): RunNamedAsync - gate-granularity ---

    // Test (f): Named check found, exits zero => Passed=true, Skipped=false, Elapsed>Zero
    [Fact]
    public async Task RunNamed_ConfiguredCheck_ExitZero_ReturnsPassed()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            Spec("version", "dotnet", new[] { "--version" })
        };

        var result = await runner.RunNamedAsync("version", specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Passed, $"configured check should pass; stderr={result.StderrTail}");
        Assert.False(result.Skipped, "configured check should not be skipped");
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Elapsed > TimeSpan.Zero, "elapsed should be > Zero");
    }

    // Test (g): Named check found, exits non-zero => Passed=false, Skipped=false, Elapsed>Zero
    [Fact]
    public async Task RunNamed_ConfiguredCheck_ExitNonZero_ReturnsFailed()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            Spec("fail", "cmd", new[] { "/c", "exit 1" })
        };

        var result = await runner.RunNamedAsync("fail", specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.Passed, "non-zero exit should be Passed=false");
        Assert.False(result.Skipped, "configured check should not be skipped");
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(result.Elapsed > TimeSpan.Zero, "elapsed should be > Zero");
    }

    // Test (h): Named check not in list => Skipped=true, Passed=false, Elapsed=Zero
    // A not-configured check must never count as a gate failure.
    [Fact]
    public async Task RunNamed_NotConfigured_ReturnsSkipped_NeverFailure()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            Spec("build", "dotnet", new[] { "build" })
        };

        var result = await runner.RunNamedAsync("typecheck", specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.True(result.Skipped, "unconfigured check should be Skipped=true");
        Assert.False(result.Passed, "skipped result should not claim Passed=true");
        Assert.Equal("typecheck", result.Name);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public async Task MissingRequiredPath_DefaultMode_StillRunsCommand()
    {
        var missing = $"missing-{Guid.NewGuid():N}";
        var spec = new CheckSpec(
            "version",
            "dotnet",
            ["--version"],
            TimeSpan.FromSeconds(30),
            RequiredPaths: [missing]);

        var result = Assert.Single(await new AutomatedChecksRunner().RunAsync(
            [spec],
            Directory.GetCurrentDirectory(),
            CancellationToken.None));

        Assert.True(result.Passed, result.StderrTail);
        Assert.False(result.Inconclusive);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdoutTail));
    }

    [Fact]
    public async Task MissingRequiredPath_OptInMode_IsInconclusive_AndCommandDoesNotRun()
    {
        var missing = $"missing-{Guid.NewGuid():N}";
        var spec = new CheckSpec(
            "requires-input",
            "this-command-must-not-run",
            Array.Empty<string>(),
            TimeSpan.FromSeconds(5),
            RequiredPaths: [missing]);

        var result = Assert.Single(await new AutomatedChecksRunner().RunAsync(
            [spec],
            Directory.GetCurrentDirectory(),
            CancellationToken.None,
            AutomatedChecksRunner.RequiredPathHandling.Inconclusive));

        Assert.False(result.Passed);
        Assert.True(result.Inconclusive);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
        Assert.Equal([missing], result.MissingRequiredPaths);
        Assert.Contains("required paths absent", result.StderrTail);
    }

    [Fact]
    public async Task RunNamed_MissingRequiredPath_DefaultMode_StillRunsCommand()
    {
        var missing = $"missing-{Guid.NewGuid():N}";
        var spec = new CheckSpec(
            "version",
            "dotnet",
            ["--version"],
            TimeSpan.FromSeconds(30),
            RequiredPaths: [missing]);

        var result = await new AutomatedChecksRunner().RunNamedAsync(
            "version",
            [spec],
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.True(result.Passed, result.StderrTail);
        Assert.False(result.Inconclusive);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdoutTail));
    }

    [Fact]
    public async Task RunNamed_MissingRequiredPath_OptInMode_IsInconclusive_AndCommandDoesNotRun()
    {
        var missing = $"missing-{Guid.NewGuid():N}";
        var spec = new CheckSpec(
            "requires-input",
            "this-command-must-not-run",
            Array.Empty<string>(),
            TimeSpan.FromSeconds(5),
            RequiredPaths: [missing]);

        var result = await new AutomatedChecksRunner().RunNamedAsync(
            "requires-input",
            [spec],
            Directory.GetCurrentDirectory(),
            CancellationToken.None,
            AutomatedChecksRunner.RequiredPathHandling.Inconclusive);

        Assert.False(result.Passed);
        Assert.True(result.Inconclusive);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
        Assert.Equal([missing], result.MissingRequiredPaths);
    }

    [Fact]
    public async Task ExistingRequiredPath_RunsCommandNormally()
    {
        var required = Path.GetFileName(typeof(AutomatedChecksRunnerTests).Assembly.Location);
        var workingDirectory = Path.GetDirectoryName(typeof(AutomatedChecksRunnerTests).Assembly.Location)!;
        var spec = new CheckSpec(
            "version",
            "dotnet",
            ["--version"],
            TimeSpan.FromSeconds(30),
            RequiredPaths: [required]);

        var result = Assert.Single(await new AutomatedChecksRunner().RunAsync(
            [spec],
            workingDirectory,
            CancellationToken.None));

        Assert.True(result.Passed, result.StderrTail);
        Assert.False(result.Inconclusive);
        Assert.Empty(result.MissingRequiredPaths ?? Array.Empty<string>());
    }

    // --- Test (e): Cancellation mid-flight ---
    // spec1: long-running sleep (~29s, cross-platform)
    // spec2: fast success (dotnet --version)
    // Cancel ~100ms after starting; spec1 should be Passed=false; spec2 should be not-run
    [Fact]
    public async Task Cancellation_MidFlight_SubsequentSpecsAreNotRun()
    {
        var runner = new AutomatedChecksRunner();
        var (sleepExe, sleepArgs) = SleepCmd(29);
        var specs = new[]
        {
            new CheckSpec(
                "long-sleep",
                sleepExe,
                sleepArgs,
                TimeSpan.FromSeconds(60)),   // generous timeout so it doesn't time out on its own
            // fast success - would complete instantly if allowed
            Spec("version", "dotnet", new[] { "--version" })
        };

        using var cts = new CancellationTokenSource();
        // Cancel after 100ms - well within the 29s sleep
        cts.CancelAfter(100);

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), cts.Token);

        Assert.Equal(2, results.Count);

        // spec1 was killed mid-flight
        Assert.False(results[0].Passed, "cancelled spec1 should be Passed=false");

        // spec2 was never started
        Assert.False(results[1].Passed, "not-run spec2 should be Passed=false");
        Assert.Equal(-1, results[1].ExitCode);
        Assert.Equal(TimeSpan.Zero, results[1].Elapsed);
    }
}
