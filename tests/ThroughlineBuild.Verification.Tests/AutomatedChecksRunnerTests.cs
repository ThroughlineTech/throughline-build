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
    // spec: sleep ~5s via "cmd /c ping -n 6 127.0.0.1 >NUL" but timeout=200ms
    // Expected: Passed=false, StderrTail contains "timeout after"
    [Fact]
    public async Task Timeout_MarksSpecAsFailed_AndAppendsTimeoutMessage()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            // ping -n 6 sends 6 pings ~1s apart = ~5s total; we timeout at 200ms
            new CheckSpec(
                "slow",
                "cmd",
                new[] { "/c", "ping -n 6 127.0.0.1 >NUL" },
                TimeSpan.FromMilliseconds(200))
        };

        var results = await runner.RunAsync(specs, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed, "timed-out spec should be Passed=false");
        Assert.Contains("timeout after", results[0].StderrTail, StringComparison.OrdinalIgnoreCase);
    }

    // --- Test (e): Cancellation mid-flight ---
    // spec1: long-running sleep (ping -n 30 = ~29s)
    // spec2: fast success (dotnet --version)
    // Cancel ~100ms after starting; spec1 should be Passed=false; spec2 should be not-run
    [Fact]
    public async Task Cancellation_MidFlight_SubsequentSpecsAreNotRun()
    {
        var runner = new AutomatedChecksRunner();
        var specs = new[]
        {
            // long sleep - ping -n 30 = ~29 seconds
            new CheckSpec(
                "long-sleep",
                "cmd",
                new[] { "/c", "ping -n 30 127.0.0.1 >NUL" },
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
