using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

/// <summary>
/// Automated Windows process-tree cleanup coverage that runs on the development
/// host (and Windows CI). It launches a real cmd -> powershell -> ping tree
/// through the production ConPTY host, then proves the forced termination path
/// (job-object kill) leaves no descendant alive. Non-Windows hosts skip it.
/// </summary>
public sealed class WindowsProcessTreeCleanupTests
{
    [Fact]
    public async Task PseudoConsoleInput_IsNotReplacedByParentRedirectedStdin()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await RunInputRoutingAsync();
    }

    [Fact]
    public async Task ForcedTermination_LeavesNoDescendantAlive()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows-only terminal host; see UnixInteractiveClaudeProcessLauncher.

        await RunWindowsAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"lattice tree {Guid.NewGuid():N}.pid");
        // powershell (child) launches a detached ping (grandchild), records ping's
        // pid, then sleeps. ping is a genuine descendant of the launched cmd root.
        var script =
            $"$p = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"[IO.File]::WriteAllText('{pidFile}', $p.Id.ToString()); Start-Sleep -Seconds 60";

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;

        var spec = new InteractiveClaudeLaunchSpec(
            "cmd.exe",
            ["/c", "powershell", "-NoProfile", "-Command", script],
            Environment.CurrentDirectory,
            environment);

        // Zero grace -> exercise the forced job-object kill path directly.
        var host = WindowsConPtyClaudeProcess.Start(spec, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        var rootPid = host.ProcessId;
        var pingPid = 0;
        try
        {
            pingPid = await ReadPidAsync(pidFile, TimeSpan.FromSeconds(25));
            Assert.True(IsAlive(pingPid), "grandchild ping should be running before termination");

            await host.TerminateAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((IsAlive(rootPid) || IsAlive(pingPid)) && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.False(IsAlive(pingPid), "descendant ping survived tree termination");
            Assert.False(IsAlive(rootPid), "root cmd survived tree termination");
        }
        finally
        {
            await host.DisposeAsync();
            TryKill(pingPid);
            TryKill(rootPid);
            try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunInputRoutingAsync()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"lattice conpty input {Guid.NewGuid():N}.txt");
        var script =
            "$value = [Console]::ReadLine(); " +
            $"[IO.File]::WriteAllText('{marker}', $value)";
        var spec = new InteractiveClaudeLaunchSpec(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-Command", script],
            Environment.CurrentDirectory,
            CaptureEnvironment());

        var host = WindowsConPtyClaudeProcess.Start(spec);
        try
        {
            // ConPTY/Windows console input submits Enter as CR. LF alone is not a
            // console Enter key and must not be used for interactive commands.
            await host.WriteInputAsync("PING\r", CancellationToken.None);
            await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(File.Exists(marker), "the ConPTY child did not consume terminal input");
            Assert.Equal("PING", File.ReadAllText(marker));
        }
        finally
        {
            await host.DisposeAsync();
            try { if (File.Exists(marker)) File.Delete(marker); } catch { }
        }
    }

    private static Dictionary<string, string?> CaptureEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;
        return environment;
    }

    private static async Task<int> ReadPidAsync(string pidFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    var text = File.ReadAllText(pidFile).Trim();
                    if (int.TryParse(text, out var pid) && pid > 0) return pid;
                }
                catch (IOException) { /* mid-write; retry */ }
            }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("The launched tree never reported its grandchild pid.");
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
