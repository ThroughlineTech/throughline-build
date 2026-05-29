using Xunit;
using ThroughlineBuild.Contracts;
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

    [Fact]
    public void BuildArgs_BypassPermissionsTrue_IncludesYolo()
    {
        var options = new GeminiOptions { BypassPermissions = true };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = GeminiAgent.BuildArgs("the brief", options, workerOptions);

        Assert.Contains("--yolo", args);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsFalse_OmitsYolo()
    {
        var options = new GeminiOptions { BypassPermissions = false };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = GeminiAgent.BuildArgs("the brief", options, workerOptions);

        Assert.DoesNotContain("--yolo", args);
    }
}
