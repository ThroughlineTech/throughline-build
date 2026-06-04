using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class HelpTopicTests
{
    private static readonly HelpTopicRegistry Registry = HelpTopicRegistry.Build();

    [Theory]
    [InlineData("exit-codes")]
    [InlineData("config")]
    public void Registry_ReturnsKnownTopics(string name)
    {
        var topic = Registry.TryGet(name);

        Assert.NotNull(topic);
        Assert.Equal(name, topic.Name);
        Assert.False(string.IsNullOrWhiteSpace(topic.Body));
    }

    [Fact]
    public void ExitCodesTopic_ContainsConsolidatedGlobalAndOverrideTables()
    {
        var output = HelpTopicRenderer.Render(Registry.TryGet("exit-codes")!);

        Assert.Contains("Global exit codes:", output);
        Assert.Contains("2  Config error, bad arguments, unknown verb, or unknown help topic.", output);
        Assert.Contains("build chain", output);
        Assert.Contains("7  StoppedAtShip.", output);
        Assert.Contains("build rework", output);
        Assert.Contains("No Rework verdict", output);
        Assert.Contains("build scaffold", output);
        Assert.Contains("Partial creation", output);
    }

    [Fact]
    public void ConfigTopic_ContainsPlanSchemaAndFlagPrecedence()
    {
        var output = HelpTopicRenderer.Render(Registry.TryGet("config")!);

        Assert.Contains("[plan] schema:", output);
        Assert.Contains("mode = \"investigate\"", output);
        Assert.Contains("promote", output);
        Assert.Contains("build plan --from-brief and build chain --from-brief override [plan].mode", output);
        Assert.Contains("Any value other than \"investigate\" or \"promote\" is a config error and exits 2.", output);
    }

    [Fact]
    public void UnknownTopicOutput_ListsValidTopics()
    {
        var output = HelpTopicRenderer.RenderUnknownTopic("digest", Registry.TopicNames);

        Assert.Contains("Unknown help topic: digest", output);
        Assert.Contains("Valid topics:", output);
        Assert.Contains("  config", output);
        Assert.Contains("  exit-codes", output);
    }

    [Fact]
    public void Tier0Footer_NamesAllValidTopics()
    {
        foreach (var name in Registry.TopicNames)
            Assert.Contains(name, Tier0Renderer.TopicFooter);
    }
}
