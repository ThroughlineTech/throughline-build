using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

/// <summary>
/// Unix counterpart of <see cref="WindowsProcessTreeCleanupTests"/>. It launches a
/// real sh -> sleep tree through the production PTY host and proves the forced
/// process-group kill leaves no descendant alive. It runs on the Linux and macOS
/// CI hosts (the Windows development host skips it).
/// </summary>
public sealed class UnixProcessTreeCleanupTests
{
    [Fact]
    public async Task ForcedTermination_LeavesNoDescendantAlive()
    {
        if (OperatingSystem.IsWindows())
            return; // PTY host is Unix-only; Windows is covered by WindowsProcessTreeCleanupTests.

        await RunUnixAsync();
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task RunUnixAsync()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"lattice-tree-{Guid.NewGuid():N}.pid");
        // sh (child) backgrounds a sleep (grandchild), records its pid, then waits so
        // the leader stays alive. sleep is a genuine descendant in the same group.
        var script = $"sleep 60 & echo $! > '{pidFile}'; wait";

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;

        var spec = new InteractiveClaudeLaunchSpec(
            "/bin/sh",
            ["-c", script],
            Environment.CurrentDirectory,
            environment);

        // Zero grace -> exercise the forced SIGKILL-the-group path directly.
        var host = UnixPtyClaudeProcess.Start(spec, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        var sleepPid = 0;
        try
        {
            sleepPid = await ReadPidAsync(pidFile, TimeSpan.FromSeconds(25));
            Assert.True(IsAlive(sleepPid), "grandchild sleep should be running before termination");

            await host.TerminateAsync(CancellationToken.None);
            await host.DisposeAsync();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (IsAlive(sleepPid) && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.False(IsAlive(sleepPid), "descendant sleep survived process-group termination");
        }
        finally
        {
            await host.DisposeAsync();
            TryKill(sleepPid);
            try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
        }
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
