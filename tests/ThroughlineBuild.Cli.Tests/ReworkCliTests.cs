using System.Diagnostics;
using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// CLI-level tests for the "build rework" verb: CliUsage assertions and subprocess
/// tests for argument validation exit codes.
/// </summary>
public class ReworkCliTests
{
    // --- CliUsage assertions ---

    [Fact]
    public void UsageText_ContainsReworkVerb()
    {
        Assert.Contains("rework", CliUsage.UsageText);
        Assert.Contains("build rework <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsReworkFeedbackFlag()
    {
        Assert.Contains("--feedback", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsReworkDebugFlag()
    {
        Assert.Contains("build rework <ticket-id> [--feedback", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsReworkExitCodes()
    {
        Assert.Contains("For 'build rework' verb only", CliUsage.UsageText);
        Assert.Contains("Implemented", CliUsage.UsageText);
        Assert.Contains("TicketNotInProgress", CliUsage.UsageText);
        Assert.Contains("NoFeedbackAvailable", CliUsage.UsageText);
        Assert.Contains("ImplementFailed", CliUsage.UsageText);
    }

    // --- subprocess tests ---

    [Fact]
    public async Task BuildBinary_ReworkWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "rework" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build rework <ticket-id>", stderr);
    }

    [Fact]
    public async Task BuildBinary_ReworkWithMultipleTicketIds_ExitsTwoWithMessage()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "rework", "TLB-1", "TLB-2" });

        Assert.Equal(2, exitCode);
        Assert.Contains("exactly one ticket ID", stderr);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsReworkVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build rework <ticket-id>", stdout);
    }

    private static string? LocateBuildExecutable()
    {
        var here = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "throughline-build.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) return null;

        var config = here.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", config, "net8.0");
        var exeName = OperatingSystem.IsWindows() ? "build.exe" : "build";
        var fullPath = Path.Combine(binDir, exeName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcess(
        string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
