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
    // All 18 known commands must be registered.
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
    [InlineData("setup")]
    [InlineData("user-guide")]
    [InlineData("op-doc")]
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
    [InlineData("setup")]
    [InlineData("user-guide")]
    [InlineData("op-doc")]
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
    [InlineData("setup")]
    [InlineData("user-guide")]
    [InlineData("op-doc")]
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
        var option = Assert.Single(help.Options, o => o.Flag == "--from-brief" && !o.IsGlobal);
        Assert.Contains("[plan] mode = \"promote\"", option.Description);
    }

    [Fact]
    public void Ship_HasNoAutoMergeAndNoPushOptions()
    {
        var help = Registry.TryGet("ship")!;
        Assert.Contains(help.Options, o => o.Flag == "--no-auto-merge" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--no-push" && !o.IsGlobal);
    }

    [Fact]
    public void Chain_HasContinuePastFailureOption()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o => o.Flag == "--continue-past-failure"  && !o.IsGlobal);
        Assert.DoesNotContain(help.Options, o => o.Flag == "--sequential");
    }

    [Fact]
    public void Chain_HasBatchImplementOption()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o =>
            o.Flag == "--batch-implement <ticket-id,...>" &&
            o.Description.Contains("ordered", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--batch-implement <ticket-id,...>", help.Usage);
    }

    [Fact]
    public void Chain_HasPerPhaseAgentOptions()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o => o.Flag == "--agent <name>" && o.Description.Contains("per-phase flags beat --agent", StringComparison.Ordinal));
        Assert.Contains(help.Options, o => o.Flag == "--agent-plan <name>"      && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-implement <name>" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-review <name>"    && !o.IsGlobal);
    }

    [Fact]
    public void Chain_DocumentsDependencyOrderedDispatch()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Examples, e => e.Annotation != null && e.Annotation.Contains("dependency order", StringComparison.Ordinal));
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
    public void Ship_DebugOptionDocumentsNoOp()
    {
        var help = Registry.TryGet("ship")!;
        Assert.Contains(help.Options, o => o.Flag == "--debug" && o.Description.Contains("no-op", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("rework")]
    public void PipelineVerbs_HaveExamples(string verb)
    {
        Assert.NotEmpty(Registry.TryGet(verb)!.Examples);
    }

    [Fact]
    public void Scaffold_HasValidateOnlyAndDryRunOptions()
    {
        var help = Registry.TryGet("scaffold")!;
        Assert.Contains(help.Options, o => o.Flag == "--validate-only" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--dry-run"       && !o.IsGlobal);
    }

    [Fact]
    public void OpDoc_HelpDocumentsSpecSubcommand()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("op-doc")!);

        Assert.Contains("op-doc spec", output);
        Assert.Contains("--print", output);
        Assert.Contains("--write", output);
        Assert.Contains("--force", output);
    }

    [Fact]
    public void New_HelpDocumentsInputDisambiguationAndDraftFlags()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("new")!);

        Assert.Contains("build new <body-path>", output);
        Assert.Contains("build new <text>", output);
        Assert.Contains("build new -", output);
        Assert.Contains("build new --print-template", output);
        Assert.Contains("If body.md exists, file it as the ticket body", output);
        Assert.Contains("not an existing file, draft from text", output);
        Assert.Contains("--review", output);
        Assert.Contains("--debug", output);
        Assert.Contains("--quiet", output);
        Assert.Contains("Print the file-mode body template and exit", output);
    }

    [Fact]
    public void Scaffold_HelpDocumentsExitOverridesAndValidationModes()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("scaffold")!);

        Assert.Contains("--validate-only", output);
        Assert.Contains("--dry-run", output);
        Assert.Contains("--accept-warnings", output);
        Assert.Contains("0  All plans and briefs created successfully", output);
        Assert.Contains("3  Partial creation", output);
    }

    [Fact]
    public void List_HelpDocumentsFilterOptions()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("list")!);

        Assert.Contains("--state <name>", output);
        Assert.Contains("--parent <id>", output);
        Assert.Contains("--type <name>", output);
    }

    [Theory]
    [InlineData("amend", "amend <ticket-id>")]
    [InlineData("close", "close <ticket-id> <reason>")]
    [InlineData("defer", "defer <ticket-id> <reason>")]
    [InlineData("reopen", "reopen <ticket-id> [reason]")]
    public void MutationVerbs_HelpShowsRequiredArguments(string verb, string expectedUsage)
    {
        var output = Tier1Renderer.Render(Registry.TryGet(verb)!);

        Assert.Contains(expectedUsage, output);
    }

    [Fact]
    public void WorkItemVerbOptions_DoNotLeakAcrossHelpBlocks()
    {
        Assert.DoesNotContain("--validate-only", Tier1Renderer.Render(Registry.TryGet("new")!));
        Assert.DoesNotContain("--review", Tier1Renderer.Render(Registry.TryGet("list")!));
        Assert.DoesNotContain("--label", Tier1Renderer.Render(Registry.TryGet("scaffold")!));
        Assert.DoesNotContain("--no-cascade", Tier1Renderer.Render(Registry.TryGet("reopen")!));
        Assert.Contains("--no-cascade", Tier1Renderer.Render(Registry.TryGet("close")!));
        Assert.Contains("--no-cascade", Tier1Renderer.Render(Registry.TryGet("defer")!));
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
    [InlineData("op-doc")]
    [InlineData("scaffold")]
    public void Tier1Renderer_CanRenderEveryCommand(string verb)
    {
        var help = Registry.TryGet(verb)!;
        var output = Tier1Renderer.Render(help);
        Assert.Contains($"build {verb} -", output);
    }
}
