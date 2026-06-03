using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for HelpRegistryFactory. Verifies that the canonical registry is
/// fully populated and that each command group contains the expected entries.
/// </summary>
public class HelpRegistryFactoryTests
{
    private static readonly HelpRegistry Registry = HelpRegistryFactory.Build();

    // ------------------------------------------------------------------
    // All 17 known commands must be registered.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("chain")]
    [InlineData("rework")]
    [InlineData("decompose")]
    [InlineData("new")]
    [InlineData("list")]
    [InlineData("amend")]
    [InlineData("close")]
    [InlineData("defer")]
    [InlineData("reopen")]
    [InlineData("init")]
    [InlineData("settarget")]
    [InlineData("user-guide")]
    [InlineData("scaffold")]
    public void TryGet_ReturnsEntryForAllKnownCommands(string verb)
    {
        var help = Registry.TryGet(verb);
        Assert.NotNull(help);
        Assert.Equal(verb, help.Name);
    }

    [Fact]
    public void TryGet_ReturnsNullForUnknownCommand()
    {
        Assert.Null(Registry.TryGet("frobnicate"));
    }

    // ------------------------------------------------------------------
    // Group membership
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("chain")]
    [InlineData("rework")]
    [InlineData("decompose")]
    public void PipelineCommands_HaveCorrectGroup(string verb)
    {
        Assert.Equal(CommandGroup.Pipeline, Registry.TryGet(verb)!.Group);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("list")]
    [InlineData("amend")]
    [InlineData("close")]
    [InlineData("defer")]
    [InlineData("reopen")]
    public void WorkItemCommands_HaveCorrectGroup(string verb)
    {
        Assert.Equal(CommandGroup.WorkItems, Registry.TryGet(verb)!.Group);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("settarget")]
    [InlineData("user-guide")]
    [InlineData("scaffold")]
    public void ConfigureCommands_HaveCorrectGroup(string verb)
    {
        Assert.Equal(CommandGroup.Configure, Registry.TryGet(verb)!.Group);
    }

    // ------------------------------------------------------------------
    // Every command must have a non-empty summary and usage.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("chain")]
    [InlineData("rework")]
    [InlineData("decompose")]
    [InlineData("new")]
    [InlineData("list")]
    [InlineData("amend")]
    [InlineData("close")]
    [InlineData("defer")]
    [InlineData("reopen")]
    [InlineData("init")]
    [InlineData("settarget")]
    [InlineData("user-guide")]
    [InlineData("scaffold")]
    public void AllCommands_HaveNonEmptySummaryAndUsage(string verb)
    {
        var help = Registry.TryGet(verb)!;
        Assert.False(string.IsNullOrWhiteSpace(help.Summary), $"{verb}: Summary must not be empty");
        Assert.False(string.IsNullOrWhiteSpace(help.Usage),   $"{verb}: Usage must not be empty");
    }

    // ------------------------------------------------------------------
    // Spot-checks for specific command data.
    // ------------------------------------------------------------------

    [Fact]
    public void Plan_HasFromBriefOption()
    {
        var help = Registry.TryGet("plan")!;
        Assert.Contains(help.Options, o => o.Flag == "--from-brief" && !o.IsGlobal);
    }

    [Fact]
    public void Ship_HasNoAutoMergeAndNoPushOptions()
    {
        var help = Registry.TryGet("ship")!;
        Assert.Contains(help.Options, o => o.Flag == "--no-auto-merge" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--no-push" && !o.IsGlobal);
    }

    [Fact]
    public void Chain_HasSequentialAndContinuePastFailureOptions()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o => o.Flag == "--sequential"             && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--continue-past-failure"  && !o.IsGlobal);
    }

    [Fact]
    public void Chain_HasPerPhaseAgentOptions()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o => o.Flag == "--agent-plan <name>"      && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-implement <name>" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-review <name>"    && !o.IsGlobal);
    }

    [Fact]
    public void Chain_HasNonStandardExitCodes()
    {
        var help = Registry.TryGet("chain")!;
        // Chain has unique exit codes (3=StoppedAtPlan, 7=StoppedAtShip).
        Assert.Contains(help.ExitCodes, ec => ec.Code == 3);
        Assert.Contains(help.ExitCodes, ec => ec.Code == 7);
    }

    [Fact]
    public void Rework_HasFeedbackOption()
    {
        var help = Registry.TryGet("rework")!;
        Assert.Contains(help.Options, o => o.Flag.StartsWith("--feedback") && !o.IsGlobal);
    }

    [Fact]
    public void Scaffold_HasValidateOnlyAndDryRunOptions()
    {
        var help = Registry.TryGet("scaffold")!;
        Assert.Contains(help.Options, o => o.Flag == "--validate-only" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--dry-run"       && !o.IsGlobal);
    }

    // ------------------------------------------------------------------
    // Tier0Renderer can render the factory registry without throwing.
    // ------------------------------------------------------------------

    [Fact]
    public void Tier0Renderer_CanRenderFactoryRegistry()
    {
        var output = Tier0Renderer.Render(Registry);
        Assert.Contains("build - Throughline Build", output);
        Assert.Contains("Pipeline:", output);
        Assert.Contains("Work items:", output);
        Assert.Contains("Configure:", output);
    }

    // ------------------------------------------------------------------
    // Tier1Renderer can render every registered command without throwing.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("chain")]
    [InlineData("rework")]
    [InlineData("decompose")]
    [InlineData("new")]
    [InlineData("list")]
    [InlineData("amend")]
    [InlineData("close")]
    [InlineData("defer")]
    [InlineData("reopen")]
    [InlineData("init")]
    [InlineData("settarget")]
    [InlineData("user-guide")]
    [InlineData("scaffold")]
    public void Tier1Renderer_CanRenderEveryCommand(string verb)
    {
        var help = Registry.TryGet(verb)!;
        var output = Tier1Renderer.Render(help);
        Assert.Contains($"build {verb} -", output);
    }
}
