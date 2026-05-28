using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexAgentStubTests
{
    [Fact]
    public void Name_IsCodex()
    {
        var agent = new CodexAgent();
        Assert.Equal("codex", agent.Name);
    }

    [Fact]
    public void Digester_IsNull()
    {
        var agent = new CodexAgent();
        Assert.Null(agent.Digester);
    }
}
