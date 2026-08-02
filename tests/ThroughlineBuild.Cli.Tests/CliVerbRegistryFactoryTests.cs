using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public sealed class CliVerbRegistryFactoryTests
{
    [Fact]
    public void BuildRegistersEveryActionVerb()
    {
        string[] expected =
        [
            "init", "settarget", "user-guide", "op-doc", "models",
            "sweep", "candidate", "worktree", "gate", "waves", "list", "get", "comments", "comment", "transition",
            "relate", "setup", "amend", "close", "defer", "reopen", "new",
            "scaffold", "rework", "decompose", "plan", "implement", "review",
            "ship", "chain",
        ];

        var registry = CliVerbRegistryFactory.Build();

        Assert.Equal(expected.Order(), registry.Verbs.Select(verb => verb.Name).Order());
    }

    [Theory]
    [InlineData("init")]
    [InlineData("settarget")]
    [InlineData("user-guide")]
    [InlineData("op-doc")]
    [InlineData("models")]
    public void BuildMarksConfigRepairVerbsForPreConfigDispatch(string name)
    {
        var registry = CliVerbRegistryFactory.Build();

        Assert.True(registry.TryGet(name, out var verb));
        Assert.NotNull(verb);
        Assert.True(verb.RunsBeforeConfig);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("worktree")]
    [InlineData("gate")]
    [InlineData("waves")]
    [InlineData("candidate")]
    [InlineData("setup")]
    [InlineData("chain")]
    public void BuildMarksConfiguredVerbsForPostConfigDispatch(string name)
    {
        var registry = CliVerbRegistryFactory.Build();

        Assert.True(registry.TryGet(name, out var verb));
        Assert.NotNull(verb);
        Assert.False(verb.RunsBeforeConfig);
    }
}
