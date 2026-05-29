using System.Collections.Generic;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Copilot;
using Xunit;

namespace ThroughlineBuild.Workers.Copilot.Tests;

public class CopilotAgentStubTests
{
    [Fact]
    public void Name_ReturnsCopilot()
    {
        var agent = new CopilotAgent();
        Assert.Equal("copilot", agent.Name);
    }

    [Fact]
    public void Digester_ReturnsNull()
    {
        var agent = new CopilotAgent();
        Assert.Null(agent.Digester);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsNotImplementedException()
    {
        var agent = new CopilotAgent();
        var brief = new Brief(
            TicketId: "TLB-234",
            Phase: Phase.Implement,
            Instruction: "test",
            RelevantFiles: System.Array.Empty<string>(),
            AllowedWrites: System.Array.Empty<string>(),
            Context: new Dictionary<string, string>());
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            agent.ExecuteAsync(brief, ".", new WorkerOptions(System.TimeSpan.FromSeconds(30)), default));
    }

    [Fact]
    public void Options_DefaultSizesHasThreeEntries()
    {
        var opts = new CopilotOptions();
        Assert.Equal(3, opts.Sizes.Count);
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Small));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Medium));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Large));
    }
}
