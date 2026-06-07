using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class VerifierToolEnforcementTests
{
    private static readonly string[] ReadOnlyTools = { "Read", "Grep", "Glob" };

    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void UnenforcedWarning_NonEnforcingAgentWithTools_ReturnsWarning(string agent)
    {
        var warning = VerifierToolEnforcement.UnenforcedWarning(agent, ReadOnlyTools);

        Assert.NotNull(warning);
        Assert.Contains("verifier_allowed_tools", warning);
        Assert.Contains(agent, warning);
        Assert.Contains("TLB-478", warning);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("Claude-Code")] // case-insensitive
    [InlineData("copilot")]
    public void UnenforcedWarning_EnforcingAgent_ReturnsNull(string agent)
    {
        Assert.Null(VerifierToolEnforcement.UnenforcedWarning(agent, ReadOnlyTools));
    }

    [Fact]
    public void UnenforcedWarning_EmptyToolList_ReturnsNull()
    {
        Assert.Null(VerifierToolEnforcement.UnenforcedWarning("codex", System.Array.Empty<string>()));
    }
}
