using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class ClaudeCodeReviewerTests
{
    // -------------------------------------------------------------------------
    // Stub IWorkerAgent - records call args; returns a configured WorkerResult
    // -------------------------------------------------------------------------
    private sealed class StubWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;

        public string Name => "stub";

        // Captured call args
        public Brief? CapturedBrief { get; private set; }
        public string? CapturedWorkingDirectory { get; private set; }
        public WorkerOptions? CapturedOptions { get; private set; }

        public StubWorkerAgent(WorkerResult result)
        {
            _result = result;
        }

        public Task<WorkerResult> ExecuteAsync(
            Brief brief,
            string workingDirectory,
            WorkerOptions options,
            CancellationToken ct)
        {
            CapturedBrief = brief;
            CapturedWorkingDirectory = workingDirectory;
            CapturedOptions = options;
            return Task.FromResult(_result);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static Ticket BuildTicket() =>
        new Ticket(
            "TLB-99",
            "Test Ticket",
            "Feature",
            TicketState.InReview,
            Size.S,
            Risk.Low,
            "<p>Description</p>",
            Array.Empty<Relation>(),
            Array.Empty<string>(),
            null);

    private static GitDiff BuildDiff() =>
        new GitDiff(
            "main",
            "ticket/tlb-99",
            new[]
            {
                new DiffEntry("src/Foo.cs", DiffKind.Added, null, 10, 0, "diff --git a/src/Foo.cs b/src/Foo.cs\n+class Foo {}")
            });

    private static WorkerResult BuildImplementerResult() =>
        new WorkerResult(
            Status.Ok,
            "Implementation complete.",
            new[] { "src/Foo.cs" },
            null,
            new Dictionary<string, object>());

    private static IReadOnlyList<CheckResult> EmptyChecks() =>
        Array.Empty<CheckResult>();

    private static WorkerResult OkResultWithMetadata(Dictionary<string, object> metadata) =>
        new WorkerResult(Status.Ok, "review complete", Array.Empty<string>(), null, metadata);

    private static ClaudeCodeReviewer BuildReviewer(
        StubWorkerAgent agent,
        string workingDir = "/repo",
        WorkerOptions? options = null)
    {
        options ??= new WorkerOptions(TimeSpan.FromMinutes(5));
        return new ClaudeCodeReviewer(
            agent,
            BuildTicket(),
            EmptyChecks(),
            options,
            workingDir);
    }

    // Implementer brief passed in as the first param to VerifyAsync (retained on public surface; not forwarded)
    private static Brief BuildImplementerBrief() =>
        new Brief("TLB-99", Phase.Implement, "implementer instruction", Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());

    // -------------------------------------------------------------------------
    // Test 1: Pass verdict with rationale and empty checks_failed
    // -------------------------------------------------------------------------
    [Fact]
    public async Task PassVerdict_WithRationale_EmptyChecksFailed()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "All good.",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Pass, verdict.Kind);
        Assert.Equal("All good.", verdict.Rationale);
        Assert.Empty(verdict.ChecksFailed);
    }

    // -------------------------------------------------------------------------
    // Test 2: Rework verdict with rationale and non-empty checks_failed
    // -------------------------------------------------------------------------
    [Fact]
    public async Task ReworkVerdict_WithRationale_NonEmptyChecksFailed()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale"] = "Tests are missing.",
            ["checks_failed"] = new List<string> { "build", "test" }
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Rework, verdict.Kind);
        Assert.Equal("Tests are missing.", verdict.Rationale);
        Assert.Equal(new[] { "build", "test" }, verdict.ChecksFailed);
    }

    // -------------------------------------------------------------------------
    // Test 3: Fail verdict with rationale
    // -------------------------------------------------------------------------
    [Fact]
    public async Task FailVerdict_WithRationale()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Fail",
            ["rationale"] = "Fundamentally broken.",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Fail, verdict.Kind);
        Assert.Equal("Fundamentally broken.", verdict.Rationale);
    }

    // -------------------------------------------------------------------------
    // Test 4: Malformed verdict string maps to Fail with rationale noting the bad value
    // -------------------------------------------------------------------------
    [Fact]
    public async Task MalformedVerdictString_MapsToFail_WithBadValueInRationale()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Approve",
            ["rationale"] = "Looks good to me.",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Fail, verdict.Kind);
        Assert.Contains("Approve", verdict.Rationale, StringComparison.Ordinal);
        Assert.Contains("malformed verdict", verdict.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 5: Missing verdict key maps to Fail
    // -------------------------------------------------------------------------
    [Fact]
    public async Task MissingVerdictKey_MapsToFail()
    {
        var metadata = new Dictionary<string, object>
        {
            ["rationale"] = "No verdict supplied.",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Fail, verdict.Kind);
        Assert.Contains("malformed verdict", verdict.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 6: Worker returns Status.Failed with FailureReason -> Fail, no metadata parse
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WorkerStatusFailed_WithFailureReason_MapsToFail_MetadataNotConsulted()
    {
        var workerResult = new WorkerResult(
            Status.Failed,
            "worker error",
            Array.Empty<string>(),
            "connection timeout",
            new Dictionary<string, object> { ["verdict"] = "Pass" }); // metadata would say Pass, but must be ignored

        var agent = new StubWorkerAgent(workerResult);
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Fail, verdict.Kind);
        Assert.Contains("connection timeout", verdict.Rationale, StringComparison.Ordinal);
        Assert.Contains("verifier worker failed", verdict.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 7: Worker returns Status.Escalate -> Fail
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WorkerStatusEscalate_MapsToFail()
    {
        var workerResult = new WorkerResult(
            Status.Escalate,
            "escalated",
            Array.Empty<string>(),
            null,
            new Dictionary<string, object>());

        var agent = new StubWorkerAgent(workerResult);
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Fail, verdict.Kind);
        Assert.Contains("verifier worker failed", verdict.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 8: checks_failed presented as JsonElement(Array) is parsed correctly
    // -------------------------------------------------------------------------
    [Fact]
    public async Task ChecksFailedAsJsonElementArray_ParsedCorrectly()
    {
        // Build a JsonElement array ["build","test"]
        var jsonDoc = JsonDocument.Parse("[\"build\",\"test\"]");
        var jsonArray = jsonDoc.RootElement;

        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale"] = "Checks failed.",
            ["checks_failed"] = (object)jsonArray
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Rework, verdict.Kind);
        Assert.Equal(new[] { "build", "test" }, verdict.ChecksFailed);
    }

    // -------------------------------------------------------------------------
    // Test 9: Verifier dispatches worker against the constructor-supplied workingDirectory
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WorkerDispatchedAgainstConstructorSuppliedWorkingDirectory()
    {
        var expectedDir = "/main-worktree/path";
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "ok",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent, workingDir: expectedDir);

        await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(expectedDir, agent.CapturedWorkingDirectory);
    }

    // -------------------------------------------------------------------------
    // Test 10: WorkerOptions passed to ExecuteAsync are the constructor-supplied options (no mutation)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WorkerOptionsPassedThrough_Unmodified()
    {
        var allowedTools = new[] { "Read", "Grep" };
        var envVars = new Dictionary<string, string> { ["MY_VAR"] = "val" };
        var options = new WorkerOptions(
            TimeSpan.FromMinutes(10),
            allowedTools,
            envVars);

        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "ok",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent, options: options);

        await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Same(options, agent.CapturedOptions);
        Assert.Equal(TimeSpan.FromMinutes(10), agent.CapturedOptions!.Timeout);
        Assert.Equal(allowedTools, agent.CapturedOptions.AllowedTools);
    }

    // -------------------------------------------------------------------------
    // Test 11: VerifyAsync builds a review brief via ReviewBriefBuilder (Phase.Review)
    // The worker receives a brief with Phase.Review, not the implementer brief's phase.
    // The brief instruction contains the verdict/rationale/checks_failed literals.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task VerifyAsync_BuildsReviewBriefViaReviewBriefBuilder()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "ok",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        // Implementer brief has Phase.Implement - worker must NOT receive it directly
        var implementerBrief = BuildImplementerBrief();

        await reviewer.VerifyAsync(
            implementerBrief,
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        var captured = agent.CapturedBrief;
        Assert.NotNull(captured);

        // Worker received a review-phase brief
        Assert.Equal(Phase.Review, captured!.Phase);

        // TicketId matches (both derive from the same ticket)
        Assert.Equal(implementerBrief.TicketId, captured.TicketId);

        // The brief phase differs from the implementer brief phase
        Assert.NotEqual(implementerBrief.Phase, captured.Phase);

        // The instruction contains the WORKER_RESULT envelope literals from ReviewBriefBuilder
        Assert.Contains("WORKER_RESULT", captured.Instruction);
        Assert.Contains("verdict", captured.Instruction);
        Assert.Contains("rationale", captured.Instruction);
        Assert.Contains("checks_failed", captured.Instruction);
    }
}
