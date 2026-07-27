using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Workers.Common.Tests;

/// <summary>
/// Abstract contract test base for <see cref="IWorkerAgent"/> implementations.
///
/// Any agent that implements <see cref="IWorkerAgent"/> must extend this class
/// and provide concrete implementations of the four abstract members. The base
/// class then enforces the following invariants via xunit [Fact] tests:
///
///   1. Name is non-null and non-empty.
///   2. Name is consistent across multiple CreateAgent() calls (no per-instance randomness).
///   3. Parsing a known-good fixture (one whose result contains a WORKER_RESULT block)
///      yields <see cref="Status.Ok"/>.
///   4. The summary from a known-good fixture is non-null and non-empty.
///   5. FilesChanged from a known-good fixture is not null (may be empty, but not null).
///   6. Parsing a known-error fixture (one whose result lacks a WORKER_RESULT block)
///      yields <see cref="Status.Failed"/>.
///   7. FailureReason from a known-error fixture is non-null and non-empty.
///
/// The parse seam (<see cref="ParseFromFixtureContent"/>) accepts the raw file
/// content and returns a <see cref="WorkerResult"/> without spawning a real
/// subprocess. Concrete subclasses route this through the agent's internal
/// envelope parser (e.g. ClaudeCodeAgent.ParseStdoutEnvelope).
/// </summary>
public abstract class IWorkerAgentContractTests
{
    /// <summary>
    /// Creates and returns a fresh agent instance under test.
    /// </summary>
    protected abstract IWorkerAgent CreateAgent();

    /// <summary>
    /// Returns the absolute path to a fixture file whose parsed result
    /// contains a valid WORKER_RESULT block, so parsing should yield
    /// <see cref="Status.Ok"/>.
    /// </summary>
    protected abstract string KnownGoodFixturePath();

    /// <summary>
    /// Returns the absolute path to a fixture file whose parsed result
    /// does NOT contain a WORKER_RESULT block, so parsing should yield
    /// <see cref="Status.Failed"/>.
    /// </summary>
    protected abstract string KnownErrorFixturePath();

    /// <summary>
    /// Parse the raw fixture file content through the agent-specific envelope
    /// parser without spawning a subprocess. Concrete subclasses call the
    /// agent's internal static parse method (e.g. ClaudeCodeAgent.ParseStdoutEnvelope).
    /// </summary>
    protected abstract WorkerResult ParseFromFixtureContent(IWorkerAgent agent, string content);

    // -------------------------------------------------------------------------
    // Name invariants
    // -------------------------------------------------------------------------

    [Fact]
    public void Name_IsNonEmpty()
    {
        var agent = CreateAgent();
        Assert.False(string.IsNullOrWhiteSpace(agent.Name),
            "IWorkerAgent.Name must not be null, empty, or whitespace-only.");
    }

    [Fact]
    public void Name_IsConsistentAcrossInstances()
    {
        var a = CreateAgent();
        var b = CreateAgent();
        Assert.Equal(a.Name, b.Name);
    }

    // -------------------------------------------------------------------------
    // Good-fixture invariants
    // -------------------------------------------------------------------------

    [Fact]
    public void GoodFixture_ReturnsOkStatus()
    {
        var agent = CreateAgent();
        var content = File.ReadAllText(KnownGoodFixturePath());
        var result = ParseFromFixtureContent(agent, content);
        Assert.Equal(Status.Ok, result.Status);
    }

    [Fact]
    public void GoodFixture_SummaryIsNonEmpty()
    {
        var agent = CreateAgent();
        var content = File.ReadAllText(KnownGoodFixturePath());
        var result = ParseFromFixtureContent(agent, content);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary),
            "WorkerResult.Summary must not be null or empty for a successful result.");
    }

    [Fact]
    public void GoodFixture_FilesChangedIsNotNull()
    {
        var agent = CreateAgent();
        var content = File.ReadAllText(KnownGoodFixturePath());
        var result = ParseFromFixtureContent(agent, content);
        Assert.NotNull(result.FilesChanged);
    }

    // -------------------------------------------------------------------------
    // Error-fixture invariants
    // -------------------------------------------------------------------------

    [Fact]
    public void ErrorFixture_ReturnsFailedStatus()
    {
        var agent = CreateAgent();
        var content = File.ReadAllText(KnownErrorFixturePath());
        var result = ParseFromFixtureContent(agent, content);
        Assert.Equal(Status.Failed, result.Status);
    }

    [Fact]
    public void ErrorFixture_FailureReasonIsNonEmpty()
    {
        var agent = CreateAgent();
        var content = File.ReadAllText(KnownErrorFixturePath());
        var result = ParseFromFixtureContent(agent, content);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason),
            "WorkerResult.FailureReason must not be null or empty when status is Failed.");
    }
}
