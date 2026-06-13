using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class ClaudeStopHookCommandTests
{
    [Fact]
    public void HiddenCommand_IsMatchedOnlyByExactPrefix()
    {
        Assert.True(ClaudeStopHookCommand.IsMatch(["internal", "claude-stop-hook", "--run-dir", "x", "--run-id", "y"]));
        Assert.False(ClaudeStopHookCommand.IsMatch(["claude-stop-hook"]));
        Assert.False(ClaudeStopHookCommand.IsMatch(["internal", "other"]));
    }

    [Fact]
    public void PublicHelp_DoesNotAdvertiseHiddenCommand()
    {
        var help = Tier0Renderer.Render(HelpRegistryFactory.Build());

        Assert.DoesNotContain("claude-stop-hook", help, StringComparison.Ordinal);
        Assert.DoesNotContain("internal", help, StringComparison.Ordinal);
    }
}
