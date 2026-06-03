using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for Tier0Renderer. Includes a golden test that pins the full rendered
/// menu output and targeted tests for specific rendering behaviours.
/// </summary>
public class Tier0RendererTests
{
    // ------------------------------------------------------------------
    // Registry builder - registers all real commands in logical order so
    // the alphabetic sort performed by the renderer is exercised.
    // ------------------------------------------------------------------

    private static HelpRegistry BuildStandardRegistry()
    {
        var r = HelpRegistry.CreateEmpty();

        // Pipeline (registered in pipeline order; renderer will sort alphabetically)
        r.Register(new CommandHelp("plan",      CommandGroup.Pipeline,  "Run the plan phase for one or more tickets",           "", [], [], []));
        r.Register(new CommandHelp("implement", CommandGroup.Pipeline,  "Run the implement phase for one or more tickets",       "", [], [], []));
        r.Register(new CommandHelp("review",    CommandGroup.Pipeline,  "Run the review phase for one or more tickets",          "", [], [], []));
        r.Register(new CommandHelp("ship",      CommandGroup.Pipeline,  "Ship one or more reviewed tickets",                    "", [], [], []));
        r.Register(new CommandHelp("chain",     CommandGroup.Pipeline,  "Run the full chain for one or more tickets",            "", [], [], []));
        r.Register(new CommandHelp("rework",    CommandGroup.Pipeline,  "Re-implement a Rework-verdict ticket",                  "", [], [], []));
        r.Register(new CommandHelp("decompose", CommandGroup.Pipeline,  "Decompose a ticket into shippable sub-tickets",         "", [], [], []));

        // WorkItems
        r.Register(new CommandHelp("new",    CommandGroup.WorkItems, "Create a new ticket",                    "", [], [], []));
        r.Register(new CommandHelp("list",   CommandGroup.WorkItems, "List tickets with optional filters",     "", [], [], []));
        r.Register(new CommandHelp("amend",  CommandGroup.WorkItems, "Amend an existing ticket",              "", [], [], []));
        r.Register(new CommandHelp("close",  CommandGroup.WorkItems, "Close a ticket",                        "", [], [], []));
        r.Register(new CommandHelp("defer",  CommandGroup.WorkItems, "Defer a ticket",                        "", [], [], []));
        r.Register(new CommandHelp("reopen", CommandGroup.WorkItems, "Reopen a closed or deferred ticket",    "", [], [], []));

        // Configure
        r.Register(new CommandHelp("init",       CommandGroup.Configure, "Write .build/config.toml from the built-in template", "", [], [], []));
        r.Register(new CommandHelp("settarget",  CommandGroup.Configure, "Set or display the resolved target branch",            "", [], [], []));
        r.Register(new CommandHelp("user-guide", CommandGroup.Configure, "Write the operator user guide",                       "", [], [], []));
        r.Register(new CommandHelp("scaffold",   CommandGroup.Configure, "Scaffold an op-doc into Plane",                       "", [], [], []));

        return r;
    }

    // ------------------------------------------------------------------
    // Golden output
    //
    // Column widths computed from the registered commands above:
    //   Pipeline  : max name = "implement" / "decompose" = 9
    //   Work items: max name = "reopen" = 6
    //   Configure : max name = "user-guide" = 10
    //   Global options: max flag = "--summary-json" = 14
    // ------------------------------------------------------------------

    private const string GoldenMenu =
        "build - Throughline Build\n\n" +
        "Pipeline:\n" +
        "  chain      Run the full chain for one or more tickets\n" +
        "  decompose  Decompose a ticket into shippable sub-tickets\n" +
        "  implement  Run the implement phase for one or more tickets\n" +
        "  plan       Run the plan phase for one or more tickets\n" +
        "  review     Run the review phase for one or more tickets\n" +
        "  rework     Re-implement a Rework-verdict ticket\n" +
        "  ship       Ship one or more reviewed tickets\n\n" +
        "Work items:\n" +
        "  amend   Amend an existing ticket\n" +
        "  close   Close a ticket\n" +
        "  defer   Defer a ticket\n" +
        "  list    List tickets with optional filters\n" +
        "  new     Create a new ticket\n" +
        "  reopen  Reopen a closed or deferred ticket\n\n" +
        "Configure:\n" +
        "  init        Write .build/config.toml from the built-in template\n" +
        "  scaffold    Scaffold an op-doc into Plane\n" +
        "  settarget   Set or display the resolved target branch\n" +
        "  user-guide  Write the operator user guide\n\n" +
        "Global options:\n" +
        "  -h, --help      Show this help\n" +
        "  -V, --version   Print build version and exit\n" +
        "  --debug         Stream worker output and capture session artifacts\n" +
        "  --quiet         Suppress the progress digest\n" +
        "  --summary-json  Emit phase completion summary as JSON\n\n" +
        "Run 'build help <topic>' for reference documentation on any topic.\n";

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Render_StandardRegistry_MatchesGoldenOutput()
    {
        var actual = Tier0Renderer.Render(BuildStandardRegistry());
        Assert.Equal(GoldenMenu, actual);
    }

    [Fact]
    public void Render_EmptyGroup_OmitsGroupFromOutput()
    {
        var r = HelpRegistry.CreateEmpty();
        r.Register(new CommandHelp("plan", CommandGroup.Pipeline, "Run the plan phase", "", [], [], []));
        // WorkItems and Configure left empty

        var output = Tier0Renderer.Render(r);

        Assert.Contains("Pipeline:", output);
        Assert.DoesNotContain("Work items:", output);
        Assert.DoesNotContain("Configure:", output);
    }

    [Fact]
    public void Render_AlwaysIncludesGlobalOptionsAndFooter()
    {
        // Even with an empty registry the static sections must appear.
        var output = Tier0Renderer.Render(HelpRegistry.CreateEmpty());

        Assert.Contains("Global options:", output);
        Assert.Contains("--summary-json", output);
        Assert.Contains("Run 'build help <topic>'", output);
    }

    [Fact]
    public void Render_CommandsWithinGroupAreSortedAlphabetically()
    {
        var r = HelpRegistry.CreateEmpty();
        // Register in reverse alphabetical order
        r.Register(new CommandHelp("ship",    CommandGroup.Pipeline, "Ship", "", [], [], []));
        r.Register(new CommandHelp("plan",    CommandGroup.Pipeline, "Plan", "", [], [], []));
        r.Register(new CommandHelp("review",  CommandGroup.Pipeline, "Review", "", [], [], []));

        var output = Tier0Renderer.Render(r);

        var planPos   = output.IndexOf("  plan  ", StringComparison.Ordinal);
        var reviewPos = output.IndexOf("  review", StringComparison.Ordinal);
        var shipPos   = output.IndexOf("  ship  ", StringComparison.Ordinal);

        Assert.True(planPos < reviewPos, "plan should appear before review");
        Assert.True(reviewPos < shipPos, "review should appear before ship");
    }
}
