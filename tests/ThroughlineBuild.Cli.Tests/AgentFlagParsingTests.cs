using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

/// <summary>
/// Tests for the agent flag extraction helper (CliArgParser.ExtractAgentFlags) and
/// the precedence logic it enables (per-phase flag beats --agent beats config).
/// </summary>
public class AgentFlagParsingTests
{
    // --- ExtractAgentFlags: basic extraction ---

    [Fact]
    public void ExtractAgentFlags_AgentFlag_ExtractsAgentAll()
    {
        var args = new[] { "chain", "TLB-1", "--agent", "fast-claude" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Equal("fast-claude", agentAll);
        Assert.Null(agentPlan);
        Assert.Null(agentImpl);
        Assert.Null(agentReview);
        // Extracted tokens removed from remaining
        Assert.Equal(new[] { "chain", "TLB-1" }, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_AgentPlanFlag_ExtractsAgentPlanOnly()
    {
        var args = new[] { "chain", "TLB-2", "--agent-plan", "planner" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Null(agentAll);
        Assert.Equal("planner", agentPlan);
        Assert.Null(agentImpl);
        Assert.Null(agentReview);
        Assert.Equal(new[] { "chain", "TLB-2" }, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_AgentImplementFlag_ExtractsAgentImplOnly()
    {
        var args = new[] { "implement", "TLB-3", "--agent-implement", "coder" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Null(agentAll);
        Assert.Null(agentPlan);
        Assert.Equal("coder", agentImpl);
        Assert.Null(agentReview);
        Assert.Equal(new[] { "implement", "TLB-3" }, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_AgentReviewFlag_ExtractsAgentReviewOnly()
    {
        var args = new[] { "review", "TLB-4", "--agent-review", "reviewer" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Null(agentAll);
        Assert.Null(agentPlan);
        Assert.Null(agentImpl);
        Assert.Equal("reviewer", agentReview);
        Assert.Equal(new[] { "review", "TLB-4" }, remaining);
    }

    // --- ExtractAgentFlags: combined flags ---

    [Fact]
    public void ExtractAgentFlags_AgentAndAgentPlan_BothExtracted()
    {
        var args = new[] { "chain", "TLB-5", "--agent", "default-agent", "--agent-plan", "special-planner" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Equal("default-agent", agentAll);
        Assert.Equal("special-planner", agentPlan);
        Assert.Null(agentImpl);
        Assert.Null(agentReview);
        Assert.Equal(new[] { "chain", "TLB-5" }, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_AllFourFlags_AllExtracted()
    {
        var args = new[]
        {
            "chain", "TLB-6",
            "--agent", "all-agent",
            "--agent-plan", "plan-agent",
            "--agent-implement", "impl-agent",
            "--agent-review", "review-agent"
        };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Equal("all-agent", agentAll);
        Assert.Equal("plan-agent", agentPlan);
        Assert.Equal("impl-agent", agentImpl);
        Assert.Equal("review-agent", agentReview);
        Assert.Equal(new[] { "chain", "TLB-6" }, remaining);
    }

    // --- ExtractAgentFlags: unknown/other args are preserved ---

    [Fact]
    public void ExtractAgentFlags_OtherFlagsPreserved()
    {
        var args = new[] { "plan", "TLB-7", "--agent", "fast", "--debug" };
        var (agentAll, _, _, _, remaining) = CliArgParser.ExtractAgentFlags(args);

        Assert.Equal("fast", agentAll);
        // --debug is not an agent flag; it must survive in remaining
        Assert.Equal(new[] { "plan", "TLB-7", "--debug" }, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_NoAgentFlags_RemainingMatchesInput()
    {
        var args = new[] { "implement", "TLB-8", "--debug", "--quiet" };
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(args);

        Assert.Null(agentAll);
        Assert.Null(agentPlan);
        Assert.Null(agentImpl);
        Assert.Null(agentReview);
        Assert.Equal(args, remaining);
    }

    [Fact]
    public void ExtractAgentFlags_EmptyArgs_AllNullAndEmptyRemaining()
    {
        var (agentAll, agentPlan, agentImpl, agentReview, remaining) =
            CliArgParser.ExtractAgentFlags(Array.Empty<string>());

        Assert.Null(agentAll);
        Assert.Null(agentPlan);
        Assert.Null(agentImpl);
        Assert.Null(agentReview);
        Assert.Empty(remaining);
    }

    // --- EffectiveAgentFor precedence logic ---
    // Tested inline here with a small helper that mirrors the logic in Program.cs.

    private static string SimulateEffectiveAgentFor(
        string phase,
        string? agentAll,
        string? agentPlanFlag,
        string? agentImplementFlag,
        string? agentReviewFlag,
        string configuredAgent)
    {
        // This mirrors the EffectiveAgentFor lambda in Program.cs.
        string AgentFor(string p) => configuredAgent;
        return phase == "plan" ? (agentPlanFlag ?? agentAll ?? AgentFor("plan")) :
               phase == "implement" ? (agentImplementFlag ?? agentAll ?? AgentFor("implement")) :
               phase == "review" ? (agentReviewFlag ?? agentAll ?? AgentFor("review")) :
               AgentFor(phase);
    }

    [Fact]
    public void EffectiveAgentFor_NoFlags_UsesConfiguredAgent()
    {
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("plan", null, null, null, null, "claude-code"));
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("implement", null, null, null, null, "claude-code"));
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("review", null, null, null, null, "claude-code"));
    }

    [Fact]
    public void EffectiveAgentFor_AgentAll_OverridesAllPhases()
    {
        Assert.Equal("fast", SimulateEffectiveAgentFor("plan", "fast", null, null, null, "claude-code"));
        Assert.Equal("fast", SimulateEffectiveAgentFor("implement", "fast", null, null, null, "claude-code"));
        Assert.Equal("fast", SimulateEffectiveAgentFor("review", "fast", null, null, null, "claude-code"));
    }

    [Fact]
    public void EffectiveAgentFor_PerPhaseFlagBeatAgentAll()
    {
        // --agent X --agent-plan Y: plan uses Y, others use X
        Assert.Equal("plan-specific", SimulateEffectiveAgentFor("plan", "all-agent", "plan-specific", null, null, "claude-code"));
        Assert.Equal("all-agent", SimulateEffectiveAgentFor("implement", "all-agent", "plan-specific", null, null, "claude-code"));
        Assert.Equal("all-agent", SimulateEffectiveAgentFor("review", "all-agent", "plan-specific", null, null, "claude-code"));
    }

    [Fact]
    public void EffectiveAgentFor_PerPhaseImplementFlag_OnlyOverridesImplement()
    {
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("plan", null, null, "impl-agent", null, "claude-code"));
        Assert.Equal("impl-agent", SimulateEffectiveAgentFor("implement", null, null, "impl-agent", null, "claude-code"));
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("review", null, null, "impl-agent", null, "claude-code"));
    }

    [Fact]
    public void EffectiveAgentFor_PerPhaseReviewFlag_OnlyOverridesReview()
    {
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("plan", null, null, null, "review-agent", "claude-code"));
        Assert.Equal("claude-code", SimulateEffectiveAgentFor("implement", null, null, null, "review-agent", "claude-code"));
        Assert.Equal("review-agent", SimulateEffectiveAgentFor("review", null, null, null, "review-agent", "claude-code"));
    }

    // --- Factory: unknown agent name produces clear error ---

    private sealed class StubWorkerAgent : IWorkerAgent
    {
        public string Name { get; }
        public IWorkerProgressDigester? Digester => null;
        public StubWorkerAgent(string name) => Name = name;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
            => throw new NotImplementedException();
    }

    [Fact]
    public void WorkerAgentFactory_UnknownAgentName_ThrowsConfigExceptionWithName()
    {
        var factory = new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                ["claude-code"] = () => new StubWorkerAgent("claude-code")
            });

        var ex = Assert.Throws<ConfigException>(() => factory.Create("no-such-agent"));
        Assert.Contains("no-such-agent", ex.Message);
        // Should also list the known agents.
        Assert.Contains("claude-code", ex.Message);
    }

    [Fact]
    public void WorkerAgentFactory_KnownAgentName_ReturnsAgent()
    {
        var factory = new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                ["claude-code"] = () => new StubWorkerAgent("claude-code")
            });

        var agent = factory.Create("claude-code");
        var stub = Assert.IsType<StubWorkerAgent>(agent);
        Assert.Equal("claude-code", stub.Name);
    }

    // --- CliUsage contains the new flags ---

    [Fact]
    public void UsageText_ContainsAgentFlagForPlan()
    {
        Assert.Contains("--agent <name>", CliUsage.UsageText);
        Assert.Contains("build plan", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_ContainsAgentFlagForChain()
    {
        Assert.Contains("--agent-plan <name>", CliUsage.UsageText);
        Assert.Contains("--agent-implement <name>", CliUsage.UsageText);
        Assert.Contains("--agent-review <name>", CliUsage.UsageText);
    }

    [Fact]
    public void UsageText_AgentFlagSectionDescribesPrecedence()
    {
        // Must describe the override semantics.
        Assert.Contains("Per-phase flag beats --agent beats config", CliUsage.UsageText);
    }
}
