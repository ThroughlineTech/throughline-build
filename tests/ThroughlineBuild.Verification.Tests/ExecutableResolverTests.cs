using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class ExecutableResolverTests
{
    [Fact]
    public void NonWindows_ReturnsInputUnchanged()
    {
        if (OperatingSystem.IsWindows()) return; // behavior is Windows-specific
        Assert.Equal("npm", ExecutableResolver.Resolve("npm"));
        Assert.Equal("dotnet", ExecutableResolver.Resolve("dotnet"));
    }

    [Fact]
    public void NameWithDirectorySeparator_IsReturnedVerbatim()
    {
        // A qualified path is trusted as-is on every OS.
        const string qualified = "/usr/local/bin/npm";
        Assert.Equal(qualified, ExecutableResolver.Resolve(qualified));
    }

    [Fact]
    public void NameWithExtension_IsReturnedVerbatim()
    {
        Assert.Equal("npm.cmd", ExecutableResolver.Resolve("npm.cmd"));
        Assert.Equal("tool.exe", ExecutableResolver.Resolve("tool.exe"));
    }

    [Fact]
    public void EmptyOrWhitespace_IsReturnedVerbatim()
    {
        Assert.Equal("", ExecutableResolver.Resolve(""));
        Assert.Equal("   ", ExecutableResolver.Resolve("   "));
    }

    [Fact]
    public void Windows_BareName_ResolvesCmdShimOnPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Synthesize a PATH dir holding "faketool.cmd" and confirm the resolver finds it.
        var dir = Path.Combine(Path.GetTempPath(), "exeresolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cmdPath = Path.Combine(dir, "faketool.cmd");
            File.WriteAllText(cmdPath, "@echo off\n");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);
                var resolved = ExecutableResolver.Resolve("faketool");
                // The resolved path may carry PATHEXT's casing (.CMD); compare case-insensitively.
                Assert.True(File.Exists(resolved), $"resolved path should exist: {resolved}");
                Assert.Equal(cmdPath, resolved, ignoreCase: true);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Windows_BareName_NotFound_ReturnsOriginal()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A name that does not exist anywhere on PATH falls back to the original so the
        // process-start error names the real missing tool.
        var resolved = ExecutableResolver.Resolve("definitely-not-a-real-tool-xyz123");
        Assert.Equal("definitely-not-a-real-tool-xyz123", resolved);
    }
}
