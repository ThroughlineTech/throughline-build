using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class WorkerAgentBuilderTests
{
    [Fact]
    public async Task Create_ClaudeInteractiveHook_MapsTransportWithoutSpawningPrint()
    {
        var config = new AgentConfig(
            Executable: "this-executable-must-not-run",
            MaxOutputTokens: null,
            Sizes: new Dictionary<WorkerSize, ModelTier>(),
            Transport: ClaudeCodeTransport.InteractiveHook);

        var agent = WorkerAgentBuilder.Create("claude-code", config);
        var result = await agent.ExecuteAsync(
            new Brief("TLB-test", Phase.Implement, "test", Array.Empty<string>(),
                Array.Empty<string>(), new Dictionary<string, string>()),
            Path.GetTempPath(),
            new WorkerOptions(TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("interactive-hook", result.FailureReason);
        Assert.DoesNotContain("executable not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }
}
