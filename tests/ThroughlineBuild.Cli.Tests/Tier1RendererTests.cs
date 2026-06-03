using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for Tier1Renderer. Includes a golden test that pins the full rendered
/// per-command help block and targeted tests for specific rendering behaviours.
/// </summary>
public class Tier1RendererTests
{
    // ------------------------------------------------------------------
    // Representative command fixture - all four sections populated.
    // ------------------------------------------------------------------

    private static CommandHelp BuildChainHelp() => new CommandHelp(
        Name:    "chain",
        Group:   CommandGroup.Pipeline,
        Summary: "Run the full chain for one or more tickets",
        Usage:   "chain <ticket-id> [<ticket-id> ...] [options]",
        Options:
        [
            new OptionDescription("--sequential", "Process tickets one at a time", false),
            new OptionDescription("--ship",        "Ship each ticket after review",  false),
        ],
        ExitCodes:
        [
            new ExitCodeEntry(0, "All tickets completed successfully"),
            new ExitCodeEntry(1, "At least one ticket failed"),
        ],
        Examples:
        [
            new UsageExample("chain TLB-123",                "Run the chain for one ticket"),
            new UsageExample("chain TLB-123 TLB-124 --ship", "Chain two tickets and ship"),
        ]
    );

    // ------------------------------------------------------------------
    // Golden output
    //
    // Column widths:
    //   Options  : max flag = "--sequential" = 12
    //              "--ship".PadRight(12) appends 6 spaces -> 8 spaces before description
    //   Exit codes: max code width = 1 (single-digit codes)
    //   Examples : max command = "chain TLB-123 TLB-124 --ship" = 28
    //              "chain TLB-123".PadRight(28) appends 15 spaces -> 17 spaces before annotation
    // ------------------------------------------------------------------

    private const string GoldenChain =
        "build chain - Run the full chain for one or more tickets\n" +
        "\n" +
        "Usage:\n" +
        "  build chain <ticket-id> [<ticket-id> ...] [options]\n" +
        "\n" +
        "Options:\n" +
        "  --sequential  Process tickets one at a time\n" +
        "  --ship        Ship each ticket after review\n" +
        "\n" +
        "Exit codes:\n" +
        "  0  All tickets completed successfully\n" +
        "  1  At least one ticket failed\n" +
        "\n" +
        "Examples:\n" +
        "  chain TLB-123                 Run the chain for one ticket\n" +
        "  chain TLB-123 TLB-124 --ship  Chain two tickets and ship\n" +
        "\n";

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Render_FullCommand_MatchesGoldenOutput()
    {
        var actual = Tier1Renderer.Render(BuildChainHelp());
        Assert.Equal(GoldenChain, actual);
    }

    [Fact]
    public void Render_GlobalOptionsFiltered_OnlyCommandSpecificOptionsShown()
    {
        var help = new CommandHelp(
            Name:    "plan",
            Group:   CommandGroup.Pipeline,
            Summary: "Run the plan phase",
            Usage:   "plan <ticket-id>",
            Options:
            [
                new OptionDescription("--agent", "Agent profile override", false),
                new OptionDescription("--debug", "Stream worker output",   true),
            ],
            ExitCodes: [],
            Examples:  []
        );

        var output = Tier1Renderer.Render(help);

        Assert.Contains("--agent", output);
        Assert.DoesNotContain("--debug", output);
    }

    [Fact]
    public void Render_AllOptionsGlobal_OmitsOptionsSection()
    {
        var help = new CommandHelp(
            Name:    "plan",
            Group:   CommandGroup.Pipeline,
            Summary: "Run the plan phase",
            Usage:   "plan <ticket-id>",
            Options:
            [
                new OptionDescription("--debug", "Stream worker output", true),
            ],
            ExitCodes: [],
            Examples:  []
        );

        var output = Tier1Renderer.Render(help);

        Assert.DoesNotContain("Options:", output);
    }

    [Fact]
    public void Render_EmptyExitCodes_OmitsExitCodesSection()
    {
        var help = new CommandHelp(
            Name:      "plan",
            Group:     CommandGroup.Pipeline,
            Summary:   "Run the plan phase",
            Usage:     "plan <ticket-id>",
            Options:   [],
            ExitCodes: [],
            Examples:  []
        );

        var output = Tier1Renderer.Render(help);

        Assert.DoesNotContain("Exit codes:", output);
    }

    [Fact]
    public void Render_EmptyExamples_OmitsExamplesSection()
    {
        var help = new CommandHelp(
            Name:      "plan",
            Group:     CommandGroup.Pipeline,
            Summary:   "Run the plan phase",
            Usage:     "plan <ticket-id>",
            Options:   [],
            ExitCodes: [],
            Examples:  []
        );

        var output = Tier1Renderer.Render(help);

        Assert.DoesNotContain("Examples:", output);
    }

    [Fact]
    public void Render_ExamplesWithoutAnnotations_SingleColumnFormat()
    {
        var help = new CommandHelp(
            Name:      "plan",
            Group:     CommandGroup.Pipeline,
            Summary:   "Run the plan phase",
            Usage:     "plan <ticket-id>",
            Options:   [],
            ExitCodes: [],
            Examples:
            [
                new UsageExample("plan TLB-123",         null),
                new UsageExample("plan TLB-123 TLB-124", null),
            ]
        );

        var output = Tier1Renderer.Render(help);

        // Each line is emitted bare with no trailing padding or separator spaces.
        Assert.Contains("  plan TLB-123\n", output);
        Assert.Contains("  plan TLB-123 TLB-124\n", output);
        Assert.DoesNotContain("  plan TLB-123  ", output);
    }
}
