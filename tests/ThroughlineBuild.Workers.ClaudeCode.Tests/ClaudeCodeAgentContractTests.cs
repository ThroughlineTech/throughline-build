using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common.Tests;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

/// <summary>
/// Concrete implementation of the IWorkerAgent contract tests for ClaudeCodeAgent.
/// Extends <see cref="IWorkerAgentContractTests"/> to prove that ClaudeCodeAgent
/// satisfies all IWorkerAgent invariants without spawning a real subprocess.
/// </summary>
public class ClaudeCodeAgentContractTests : IWorkerAgentContractTests
{
    protected override IWorkerAgent CreateAgent() => new ClaudeCodeAgent();

    protected override string KnownGoodFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "stream-json-worker-result-ok.ndjson");

    protected override string KnownErrorFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "stream-json-hello.ndjson");

    /// <summary>
    /// Routes the raw NDJSON fixture content through ClaudeCodeAgent.ParseStdoutEnvelope
    /// without spawning a subprocess. exitCode is 0 to exercise the parse path
    /// rather than the exit-code-failure short-circuit.
    /// </summary>
    protected override WorkerResult ParseFromFixtureContent(IWorkerAgent agent, string content)
        => ClaudeCodeAgent.ParseStdoutEnvelope(content, exitCode: 0, stderr: string.Empty);
}

/// <summary>
/// Negative contract example: a deliberately broken IWorkerAgent whose Name is
/// empty. This class is NOT a test class - it exists to show the invariant that
/// IWorkerAgentContractTests.Name_IsNonEmpty would catch at compile time (if the
/// contract were enforced structurally) and will fail the base-class [Fact] test.
///
/// Usage: run the suite against BrokenWorkerAgent to confirm the harness rejects it.
/// BrokenWorkerAgent is declared here as documentation only; it is not wired into
/// the xunit discovery by default because its host class is not a test class.
/// </summary>
internal sealed class BrokenWorkerAgent : IWorkerAgent
{
    // Intentionally violates the Name_IsNonEmpty invariant.
    public string Name => string.Empty;
    public IWorkerProgressDigester? Digester => null;

    public Task<WorkerResult> ExecuteAsync(
        Brief brief,
        string workingDirectory,
        WorkerOptions options,
        CancellationToken ct)
        => Task.FromResult(new WorkerResult(
            Status.Failed,
            "broken agent always fails",
            Array.Empty<string>(),
            "intentionally broken for contract test documentation",
            new Dictionary<string, object>()));
}

/// <summary>
/// Demonstrates directly that BrokenWorkerAgent violates the Name_IsNonEmpty
/// invariant that IWorkerAgentContractTests enforces. This serves as an
/// inline negative-contract check.
/// </summary>
public class BrokenWorkerAgentNegativeContractTest
{
    [Fact]
    public void BrokenAgent_Name_IsEmpty_ViolatesContract()
    {
        IWorkerAgent broken = new BrokenWorkerAgent();

        // This assertion mirrors Name_IsNonEmpty from IWorkerAgentContractTests,
        // and proves the broken agent would fail that invariant.
        Assert.True(
            string.IsNullOrWhiteSpace(broken.Name),
            "BrokenWorkerAgent.Name must be empty to demonstrate the invariant violation.");
    }
}
