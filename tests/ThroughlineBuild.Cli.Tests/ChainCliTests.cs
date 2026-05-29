using System.Diagnostics;
using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// CLI-level tests for the "build chain" verb: CliUsage assertions and subprocess
/// tests for argument validation exit codes.
/// </summary>
public class ChainCliTests
{
    // --- CliUsage assertions ---

    [Fact]
    public void UsageText_ContainsChainVerb()
    {
        Assert.Contains("chain", CliUsage.UsageText);
        Assert.Contains("build chain <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsChainExitCodes()
    {
        // The chain verb has its own exit code table in UsageText.
        Assert.Contains("For 'build chain' verb only", CliUsage.UsageText);
        Assert.Contains("ChainOutcome.Completed", CliUsage.UsageText);
        Assert.Contains("RefusedInitialState", CliUsage.UsageText);
        Assert.Contains("StoppedAtPlan", CliUsage.UsageText);
        Assert.Contains("ReworkCapExceeded", CliUsage.UsageText);
        Assert.Contains("StoppedAtShip", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsChainDebugFlag()
    {
        // Chain usage line must document [--debug].
        // TLB-191 added agent flags before --debug on the chain line.
        Assert.Contains("[--debug]", CliUsage.UsageText);
        Assert.Contains("build chain <ticket-id>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_DocumentsStreamingBehavior()
    {
        // Chain line must mention streaming per-phase output.
        Assert.Contains("streams per-phase output", CliUsage.UsageText);
    }

    // --- subprocess tests ---

    [Fact]
    public async Task BuildBinary_ChainWithoutTicketId_ExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, _, stderr) = await RunProcess(exe, new[] { "chain" });

        Assert.Equal(2, exitCode);
        Assert.Contains("ticket-id is required", stderr);
        Assert.Contains("build chain <ticket-id>", stderr);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_OutputContainsChainVerb()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, _) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Contains("build chain <ticket-id>", stdout);
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

        var binDir = Path.Combine(dir.FullName, "src", "ThroughlineBuild.Cli", "bin", "Debug", "net8.0");
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
