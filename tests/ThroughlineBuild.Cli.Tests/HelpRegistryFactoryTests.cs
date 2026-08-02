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
    // All known commands must be registered.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("ship")]
    [InlineData("chain")]
    [InlineData("rework")]
    [InlineData("decompose")]
    [InlineData("candidate")]
    [InlineData("worktree")]
    [InlineData("gate")]
    [InlineData("waves")]
    [InlineData("sop")]
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
    [InlineData("candidate")]
    [InlineData("worktree")]
    [InlineData("gate")]
    [InlineData("waves")]
    [InlineData("sop")]
    public void DeterministicCommands_AreGroupedForCallerOwnedConductors(string verb)
    {
        Assert.Equal(CommandGroup.Conductor, Registry.TryGet(verb)!.Group);
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
    [InlineData("candidate")]
    [InlineData("worktree")]
    [InlineData("gate")]
    [InlineData("waves")]
    [InlineData("sop")]
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
        Assert.False(string.IsNullOrWhiteSpace(help.Usage), $"{verb}: Usage must not be empty");
    }

    // ------------------------------------------------------------------
    // Spot-checks for specific command data.
    // ------------------------------------------------------------------

    [Fact]
    public void Plan_HasFromBriefOption()
    {
        var help = Registry.TryGet("plan")!;
        var option = Assert.Single(help.Options, o => o.Flag == "--from-brief" && !o.IsGlobal);
        Assert.Contains("Explicitly promote", option.Description);
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
        Assert.Contains(help.Options, o => o.Flag == "--continue-past-failure" && !o.IsGlobal);
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
    public void Gate_HasRequireChecksOption()
    {
        var help = Registry.TryGet("gate")!;

        Assert.Contains("--require-checks", help.Usage);
        Assert.Contains(help.Options, o => o.Flag == "--require-checks" && !o.IsGlobal);
    }

    [Fact]
    public void Candidate_HelpDocumentsStatusFingerprintFields()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("candidate")!);

        Assert.Contains("candidate status --ticket <id> --base <ref>", output);
        Assert.Contains("trackedDiffHash", output);
        Assert.Contains("cachedDiffHash", output);
        Assert.Contains("untrackedHash", output);
        Assert.Contains("lease", output);
        Assert.Contains("dirtyState", output);
    }

    [Fact]
    public void Sop_HelpDocumentsDoctorShapeOnlyInvariantValidation()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("sop")!);

        Assert.Contains("sop list", output);
        Assert.Contains("sop doctor", output);
        Assert.Contains("sop brief <name>", output);
        Assert.Contains("Unknown SOP name", output);
        Assert.Contains("Review invariants are structured prose", output);
        Assert.Contains("does not evaluate whether a statement is true", output);
    }

    [Fact]
    public void Chain_HasPerPhaseAgentOptions()
    {
        var help = Registry.TryGet("chain")!;
        Assert.Contains(help.Options, o => o.Flag == "--agent <name>" && o.Description.Contains("per-phase flags beat --agent", StringComparison.Ordinal));
        Assert.Contains(help.Options, o => o.Flag == "--agent-plan <name>" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-implement <name>" && !o.IsGlobal);
        Assert.Contains(help.Options, o => o.Flag == "--agent-review <name>" && !o.IsGlobal);
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
        Assert.Contains(help.Options, o => o.Flag == "--dry-run" && !o.IsGlobal);
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
    public void New_HelpDocumentsStrictJsonContractAndCapabilitySafeType()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("new")!);

        Assert.Contains("Strict JSON draft contract:", output);
        Assert.Contains("title: string, non-empty", output);
        Assert.Contains("acceptanceCriteria: one Markdown string", output);
        Assert.Contains("a JSON array is invalid", output);
        Assert.Contains("Unknown fields are rejected", output);
        Assert.Contains("Omitting type sends no explicit type assignment", output);
        Assert.Contains("work-item types", output);
        Assert.DoesNotContain("\"type\":\"task\"", output);
    }

    [Fact]
    public void New_HelpDocumentsEveryRelationKindAndIntentMapping()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("new")!);

        Assert.Contains("relates_to, duplicate, blocked_by, blocking, start_before, start_after, finish_before, finish_after, implemented_by, implements", output);
        Assert.Contains("A depends on B", output);
        Assert.Contains("\"kind\":\"blocked_by\"", output);
        Assert.Contains("A blocks B", output);
        Assert.Contains("\"kind\":\"blocking\"", output);
        Assert.Contains("A duplicates B", output);
        Assert.Contains("\"kind\":\"duplicate\"", output);
        Assert.Contains("A is related to B", output);
        Assert.Contains("\"kind\":\"relates_to\"", output);
        Assert.Contains("spaces and hyphens", output);
        Assert.Contains("inverse edges", output);
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

    [Fact]
    public void Amend_HelpDocumentsEveryMetadataOptionAndRepeatability()
    {
        var output = Tier1Renderer.Render(Registry.TryGet("amend")!);

        Assert.Contains("--title", output);
        Assert.Contains("--priority", output);
        Assert.Contains("urgent, high, medium, low, or none", output);
        Assert.Contains("--type", output);
        Assert.Contains("--label-add", output);
        Assert.Contains("repeat the option", output);
        Assert.Contains("--label-remove", output);
        Assert.Contains("--parent", output);
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
        Assert.Contains("Bring your own conductor:", output);
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
    [InlineData("candidate")]
    [InlineData("worktree")]
    [InlineData("gate")]
    [InlineData("waves")]
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
