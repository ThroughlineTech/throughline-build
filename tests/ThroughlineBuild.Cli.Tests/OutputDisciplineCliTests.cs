using System.Diagnostics;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class OutputDisciplineCliTests
{
    [Fact]
    public async Task BuildBinary_UnknownSubcommand_PrintsBriefErrorToStderrAndExitsTwo()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, stderr) = await RunProcess(exe, new[] { "wat" });

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Unknown subcommand: wat", stderr);
        Assert.Contains("See 'build --help'.", stderr);
        Assert.DoesNotContain("Usage:", stderr);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_PrintsToStdoutAndLeavesStderrEmpty()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, stderr) = await RunProcess(exe, new[] { "--help" });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("build - Throughline Build", stdout);
        Assert.Contains("Global options:", stdout);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildBinary_CommandHelp_PrintsToStdoutAndLeavesStderrEmpty()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var (exitCode, stdout, stderr) = await RunProcess(exe, new[] { "plan", "--help" });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("build plan -", stdout);
        Assert.Contains("Usage:", stdout);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildBinary_HelpFlag_WithNoColor_ProducesPlainOutput()
    {
        var exe = LocateBuildExecutable();
        if (exe is null) return;

        var env = new Dictionary<string, string> { ["NO_COLOR"] = "1" };
        var (exitCode, stdout, stderr) = await RunProcess(exe, new[] { "--help" }, env);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("build - Throughline Build", stdout);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
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
        string exe,
        string[] args,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
