using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class ClaudeTransportPreflightTests
{
    private static WorkersConfig Workers(params (string name, ClaudeCodeTransport transport, string exe)[] agents)
    {
        var dict = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
        foreach (var (name, transport, exe) in agents)
            dict[name] = new AgentConfig(exe, null, new Dictionary<WorkerSize, ModelTier>(), Transport: transport);
        return new WorkersConfig("claude-code", 30, dict, new Dictionary<string, string>());
    }

    [Fact]
    public async Task Gate_PrintAgent_ReturnsZero_AndDoesNotProbe()
    {
        // exe deliberately bogus: a print agent must not probe it, so the gate stays silent.
        var workers = Workers(("claude-code", ClaudeCodeTransport.Print, "this-must-not-run"));
        var err = new StringWriter();
        var code = await ClaudeTransportPreflight.GateAsync(workers, new[] { "claude-code" }, err, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("", err.ToString());
    }

    [Fact]
    public async Task Gate_NonClaudeAgent_IsSkipped()
    {
        var workers = Workers(("codex", ClaudeCodeTransport.InteractiveHook, "this-must-not-run"));
        var err = new StringWriter();
        var code = await ClaudeTransportPreflight.GateAsync(workers, new[] { "codex" }, err, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("", err.ToString());
    }

    [Fact]
    public async Task Gate_InteractiveAgent_MissingExecutable_FailsClearly_WithNoSilentFallback()
    {
        var workers = Workers(("claude-code", ClaudeCodeTransport.InteractiveHook, "this-claude-must-not-exist"));
        var err = new StringWriter();
        var code = await ClaudeTransportPreflight.GateAsync(workers, new[] { "claude-code" }, err, CancellationToken.None);
        Assert.NotEqual(0, code);
        var message = err.ToString();
        Assert.Contains("interactive-hook", message);
        Assert.DoesNotContain("falling back", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using print", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Report_InteractiveAgent_MissingExecutable_PrintsUnsupported_AndReturnsNonZero()
    {
        var workers = Workers(("claude-code", ClaudeCodeTransport.InteractiveHook, "this-claude-must-not-exist"));
        var outWriter = new StringWriter();
        var err = new StringWriter();
        var code = await ClaudeTransportPreflight.ReportAsync(workers, outWriter, err, CancellationToken.None);
        Assert.NotEqual(0, code);
        Assert.Contains("UNSUPPORTED", outWriter.ToString());
    }

    [Fact]
    public async Task Report_PrintAgent_PrintsOk_AndReturnsZero()
    {
        var workers = Workers(("claude-code", ClaudeCodeTransport.Print, "claude"));
        var outWriter = new StringWriter();
        var err = new StringWriter();
        var code = await ClaudeTransportPreflight.ReportAsync(workers, outWriter, err, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("OK", outWriter.ToString());
    }

    [Theory]
    [InlineData("implement", "implement")]
    [InlineData("review", "review")]
    [InlineData("rework", "implement")]
    [InlineData("decompose", "decompose")]
    public void PhasesToGateForVerb_PhaseVerb_GatesItsWorkerPhase(string verb, string expectedPhase)
    {
        // planIsPromote/fromBrief are irrelevant for non-plan verbs.
        Assert.Equal(new[] { expectedPhase }, ClaudeTransportPreflight.PhasesToGateForVerb(verb, planIsPromote: true, fromBrief: false));
    }

    [Fact]
    public void PhasesToGateForVerb_ChainPromote_GatesImplementAndReview()
    {
        Assert.Equal(new[] { "implement", "review" },
            ClaudeTransportPreflight.PhasesToGateForVerb("chain", planIsPromote: true, fromBrief: false));
    }

    [Fact]
    public void PhasesToGateForVerb_ChainInvestigate_GatesPlanImplementAndReview()
    {
        Assert.Equal(new[] { "plan", "implement", "review" },
            ClaudeTransportPreflight.PhasesToGateForVerb("chain", planIsPromote: false, fromBrief: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhasesToGateForVerb_Plan_WithoutFromBrief_AlwaysGatesPlan(bool planIsPromote)
    {
        Assert.Equal(new[] { "plan" },
            ClaudeTransportPreflight.PhasesToGateForVerb("plan", planIsPromote, fromBrief: false));
    }

    [Fact]
    public void PhasesToGateForVerb_Plan_FromBrief_GatesNothing()
    {
        Assert.Empty(ClaudeTransportPreflight.PhasesToGateForVerb("plan", planIsPromote: false, fromBrief: true));
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("list")]
    [InlineData("setup")]
    public void PhasesToGateForVerb_NonWorkerVerb_GatesNothing(string verb)
    {
        Assert.Empty(ClaudeTransportPreflight.PhasesToGateForVerb(verb, planIsPromote: true, fromBrief: false));
    }
}
