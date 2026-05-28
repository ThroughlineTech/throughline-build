using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class WorkerAgentFactoryTests
{
    // Minimal IWorkerAgent stub used by factory tests.
    private sealed class StubWorkerAgent : IWorkerAgent
    {
        public StubWorkerAgent(string name) => Name = name;
        public string Name { get; }
        public Task<WorkerResult> ExecuteAsync(
            Brief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct)
            => throw new NotImplementedException();
    }

    private static WorkerAgentFactory MakeFactory()
    {
        return new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                ["claude-code"] = () => new StubWorkerAgent("claude-code")
            });
    }

    [Fact]
    public void Create_KnownAgent_ReturnsAgentWithMatchingName()
    {
        var factory = MakeFactory();
        var agent = factory.Create("claude-code");
        Assert.Equal("claude-code", agent.Name);
    }

    [Fact]
    public void Create_UnknownAgent_ThrowsConfigException()
    {
        var factory = MakeFactory();
        var ex = Assert.Throws<ConfigException>(() => factory.Create("codex"));
        Assert.NotNull(ex);
        Assert.Contains("codex", ex.Message);
        Assert.Contains("claude-code", ex.Message);
    }

    [Fact]
    public void Create_UnknownAgent_MessageListsKnownAgents()
    {
        var factory = new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                ["agent-a"] = () => new StubWorkerAgent("agent-a"),
                ["agent-b"] = () => new StubWorkerAgent("agent-b")
            });
        var ex = Assert.Throws<ConfigException>(() => factory.Create("missing"));
        Assert.Contains("agent-a", ex.Message);
        Assert.Contains("agent-b", ex.Message);
    }
}
