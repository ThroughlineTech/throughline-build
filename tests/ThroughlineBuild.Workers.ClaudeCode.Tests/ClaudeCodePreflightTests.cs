using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public class ClaudeCodePreflightTests
{
    [Theory]
    [InlineData("2.1.177 (Claude Code)", 2, 1, 177)]
    [InlineData("2.1.177", 2, 1, 177)]
    [InlineData("claude 2.2.0\n", 2, 2, 0)]
    [InlineData("2.1", 2, 1, 0)]
    public void ParseVersion_ExtractsLeadingSemver(string raw, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), ClaudeCodePreflight.ParseVersion(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no version token here")]
    public void ParseVersion_ReturnsNull_WhenNoToken(string? raw)
    {
        Assert.Null(ClaudeCodePreflight.ParseVersion(raw));
    }

    [Fact]
    public void Evaluate_Print_IsAlwaysSupported_WithNoVersionGate()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.Print, executableResolved: false, rawVersionOutput: null);
        Assert.True(result.Supported);
        Assert.Equal(ClaudePreflightFailureKind.None, result.Kind);
    }

    [Fact]
    public void Evaluate_Interactive_SupportedAtReferenceVersion()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.InteractiveHook, executableResolved: true, "2.1.177 (Claude Code)");
        Assert.True(result.Supported);
        Assert.Equal(ClaudeCodePreflight.MinimumInteractiveVersion, result.DetectedVersion);
    }

    [Fact]
    public void Evaluate_Interactive_SupportedAboveMinimum()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.InteractiveHook, executableResolved: true, "2.3.0");
        Assert.True(result.Supported);
    }

    [Fact]
    public void Evaluate_Interactive_TooOld_FailsWithDistinctActionableMessage()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.InteractiveHook, executableResolved: true, "2.1.150");
        Assert.False(result.Supported);
        Assert.Equal(ClaudePreflightFailureKind.VersionTooOld, result.Kind);
        Assert.Contains("interactive-hook", result.Message);
        Assert.Contains("2.1.177", result.Message);
        Assert.Contains("transport = \"print\"", result.Message); // rollback hint
        // Distinguishes a capability gap from a quota/model/permission/protocol failure.
        Assert.Contains("capability check", result.Message);
    }

    [Fact]
    public void Evaluate_Interactive_UndetectableVersion_Fails()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.InteractiveHook, executableResolved: true, "garbage with no number");
        Assert.False(result.Supported);
        Assert.Equal(ClaudePreflightFailureKind.VersionUndetectable, result.Kind);
    }

    [Fact]
    public void Evaluate_Interactive_ExecutableNotFound_Fails()
    {
        var result = ClaudeCodePreflight.Evaluate(
            ClaudeCodeTransport.InteractiveHook, executableResolved: false, rawVersionOutput: null);
        Assert.False(result.Supported);
        Assert.Equal(ClaudePreflightFailureKind.ExecutableNotFound, result.Kind);
    }

    [Fact]
    public async Task CheckAsync_MissingExecutable_ReportsNotFound_NeverFallsBack()
    {
        var result = await ClaudeCodePreflight.CheckAsync(
            "this-claude-executable-must-not-exist", ClaudeCodeTransport.InteractiveHook, CancellationToken.None);
        Assert.False(result.Supported);
        Assert.Equal(ClaudePreflightFailureKind.ExecutableNotFound, result.Kind);
    }

    [Fact]
    public async Task CheckAsync_Print_SkipsProbe_AndIsSupported_EvenWithMissingExecutable()
    {
        var result = await ClaudeCodePreflight.CheckAsync(
            "this-claude-executable-must-not-exist", ClaudeCodeTransport.Print, CancellationToken.None);
        Assert.True(result.Supported);
    }
}
