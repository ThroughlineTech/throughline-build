using Xunit;

namespace ThroughlineBuild.Commands.Tests;

public sealed class CliVerbRegistryTests
{
    [Fact]
    public void RegisterAndResolvePreservesBootstrapMetadata()
    {
        var registry = new CliVerbRegistry();
        registry.Register(new CliVerb("init", CliVerbKind.Init, RunsBeforeConfig: true));

        Assert.True(registry.TryGet("init", out var verb));
        Assert.NotNull(verb);
        Assert.True(verb.RunsBeforeConfig);
    }

    [Fact]
    public void RegisterRejectsDuplicateVerbNames()
    {
        var registry = new CliVerbRegistry();
        registry.Register(new CliVerb("list", CliVerbKind.List, RunsBeforeConfig: false));

        Assert.Throws<InvalidOperationException>(
            () => registry.Register(new CliVerb("list", CliVerbKind.List, RunsBeforeConfig: false)));
    }
}
