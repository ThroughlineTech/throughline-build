using Xunit;
using ThroughlineBuild.Workers.Gemini;

namespace ThroughlineBuild.Workers.Gemini.Tests;

public class GeminiAgentStubTests
{
    [Fact]
    public void GeminiAgent_Name_IsGemini()
    {
        var agent = new GeminiAgent();
        Assert.Equal("gemini", agent.Name);
    }

    [Fact]
    public void GeminiAgent_Digester_IsNonNull()
    {
        var agent = new GeminiAgent();
        Assert.NotNull(agent.Digester);
    }
}
