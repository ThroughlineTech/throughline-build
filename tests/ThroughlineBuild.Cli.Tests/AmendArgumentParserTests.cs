using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class AmendArgumentParserTests
{
    [Fact]
    public void TryParse_captures_scalars_and_every_repeatable_label()
    {
        string[] args =
        [
            "amend", "TLB-563",
            "--title", "New title",
            "--priority", "HIGH",
            "--label-add", "bug",
            "--label-add", "cli",
            "--label-remove", "stale",
            "--parent", "TLB-500"
        ];

        var parsed = AmendArgumentParser.TryParse("TLB-563", args, 2, out var context, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(context);
        Assert.Equal("New title", context.Args["title"]);
        Assert.Equal("HIGH", context.Args["priority"]);
        Assert.Equal("TLB-500", context.Args["parent"]);
        Assert.Equal(["bug", "cli"], context.GetValues("label-add"));
        Assert.Equal(["stale"], context.GetValues("label-remove"));
    }

    [Theory]
    [InlineData("--label-add")]
    [InlineData("--title")]
    public void TryParse_rejects_options_without_values(string option)
    {
        string[] args = ["amend", "TLB-563", option];

        var parsed = AmendArgumentParser.TryParse("TLB-563", args, 2, out var context, out var error);

        Assert.False(parsed);
        Assert.Null(context);
        Assert.Equal($"{option} requires a value", error);
    }

    [Fact]
    public void TryParse_rejects_unknown_options()
    {
        string[] args = ["amend", "TLB-563", "--assignee", "alice"];

        var parsed = AmendArgumentParser.TryParse("TLB-563", args, 2, out _, out var error);

        Assert.False(parsed);
        Assert.Equal("unknown amend option '--assignee'", error);
    }

    [Fact]
    public void TryParse_rejects_repeated_scalar_options()
    {
        string[] args = ["amend", "TLB-563", "--title", "one", "--title", "two"];

        var parsed = AmendArgumentParser.TryParse("TLB-563", args, 2, out _, out var error);

        Assert.False(parsed);
        Assert.Equal("--title may only be specified once", error);
    }
}
