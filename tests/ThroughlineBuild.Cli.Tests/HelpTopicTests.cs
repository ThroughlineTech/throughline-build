using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class HelpTopicTests
{
    private static readonly HelpTopicRegistry Registry = HelpTopicRegistry.Build();

    [Theory]
    [InlineData("exit-codes")]
    [InlineData("config")]
    [InlineData("digest")]
    [InlineData("summary")]
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
    public void DigestTopic_ContainsProgressDigestBehaviorAndBuildProgressOverride()
    {
        var output = HelpTopicRenderer.Render(Registry.TryGet("digest")!);

        Assert.Contains("Progress digest:", output);
        Assert.Contains("one-line digest per worker stream event", output);
        Assert.Contains("stderr is redirected", output);
        Assert.Contains("BUILD_PROGRESS=1", output);
        Assert.Contains("does not beat --quiet or --debug", output);
    }

    [Fact]
    public void SummaryTopic_ContainsSummaryContractAndJsonOutputBehavior()
    {
        var output = HelpTopicRenderer.Render(Registry.TryGet("summary")!);

        Assert.Contains("Summary contract:", output);
        Assert.Contains("deterministic completion summary", output);
        Assert.Contains("--summary-json emits", output);
        Assert.Contains("JSON object on stdout", output);
        Assert.Contains("trim- and AOT-safe", output);
    }

    [Fact]
    public void UnknownTopicOutput_ListsValidTopics()
    {
        var output = HelpTopicRenderer.RenderUnknownTopic("missing", Registry.TopicNames);

        Assert.Contains("Unknown help topic: missing", output);
        Assert.Contains("Valid topics:", output);
        Assert.Contains("  config", output);
        Assert.Contains("  digest", output);
        Assert.Contains("  exit-codes", output);
        Assert.Contains("  summary", output);
    }

    [Fact]
    public void Tier0Footer_NamesAllValidTopics()
    {
        foreach (var name in Registry.TopicNames)
            Assert.Contains(name, Tier0Renderer.TopicFooter);
    }

    [Fact]
    public void Tier0Help_DoesNotInlineDigestOrSummaryReferenceBodies()
    {
        var output = Tier0Renderer.Render(HelpRegistryFactory.Build());

        Assert.DoesNotContain("BUILD_PROGRESS=1", output);
        Assert.DoesNotContain("Summary contract:", output);
        Assert.DoesNotContain("one-line digest per worker stream event", output);
        Assert.DoesNotContain("JSON object on stdout", output);
    }
}
