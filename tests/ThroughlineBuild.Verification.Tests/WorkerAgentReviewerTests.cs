using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class WorkerAgentReviewerTests
{
    // -------------------------------------------------------------------------
    // Stub IWorkerAgent - records call args; returns a configured WorkerResult
    // -------------------------------------------------------------------------
    private sealed class StubWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;

        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;

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
            "ticket-uuid-99",
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

    private static WorkerAgentReviewer BuildReviewer(
        StubWorkerAgent agent,
        string workingDir = "/repo",
        WorkerOptions? options = null)
    {
        options ??= new WorkerOptions(TimeSpan.FromMinutes(5));
        return new WorkerAgentReviewer(
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

    // -------------------------------------------------------------------------
    // Test 12: LastWorkerResult is populated after VerifyAsync
    // -------------------------------------------------------------------------
    [Fact]
    public async Task LastWorkerResult_PopulatedAfterVerifyAsync()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "ok",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.NotNull(reviewer.LastWorkerResult);
        // Verify it matches the stub agent's returned result
        Assert.Equal(Status.Ok, reviewer.LastWorkerResult.Status);
        Assert.Equal("review complete", reviewer.LastWorkerResult.Summary);
    }

    // -------------------------------------------------------------------------
    // Test 13: rationale_ref resolves from REVIEW_CRITIQUE fenced block -
    // content with quotes, backticks, backslashes survives without JSON-escape issues
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RationaleRef_ResolvesFromReviewCritiqueBlock_QuoteHeavyContent()
    {
        // Content that would break JSON encoding if embedded directly in the envelope.
        const string critique = "The function `foo()` returns `null` unexpectedly.\n" +
            "Line 42: `return bar ?? \"default\"` should be `return bar ?? string.Empty`.\n" +
            "Also: backslash test C:\\Users\\foo and \"quoted string\" here.";

        var blocks = new Dictionary<string, string>
        {
            ["REVIEW_CRITIQUE"] = critique
        };
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale_ref"] = "REVIEW_CRITIQUE",
            ["checks_failed"] = new List<string> { "unit-tests" }
        };
        var workerResult = new WorkerResult(Status.Ok, "review complete", Array.Empty<string>(), null, metadata, blocks);
        var agent = new StubWorkerAgent(workerResult);
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Rework, verdict.Kind);
        Assert.Equal(critique, verdict.Rationale);
        Assert.Equal(new[] { "unit-tests" }, verdict.ChecksFailed);
    }

    // -------------------------------------------------------------------------
    // Test 14: rationale_ref takes precedence over direct "rationale" key
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RationaleRef_TakesPrecedenceOverDirectRationaleKey()
    {
        const string blockContent = "critique from block";
        var blocks = new Dictionary<string, string>
        {
            ["REVIEW_CRITIQUE"] = blockContent
        };
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale_ref"] = "REVIEW_CRITIQUE",
            ["rationale"] = "this should be ignored",
            ["checks_failed"] = new List<string>()
        };
        var workerResult = new WorkerResult(Status.Ok, "review complete", Array.Empty<string>(), null, metadata, blocks);
        var agent = new StubWorkerAgent(workerResult);
        var reviewer = BuildReviewer(agent);

        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        Assert.Equal(VerdictKind.Pass, verdict.Kind);
        Assert.Equal(blockContent, verdict.Rationale);
    }

    // -------------------------------------------------------------------------
    // Test 15: Review brief contains read-only-git constraint
    // The template must state the verifier is read-only with respect to git,
    // naming stash, checkout, reset, and rebase as forbidden.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task ReviewBrief_ContainsReadOnlyGitConstraint()
    {
        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Pass",
            ["rationale"] = "ok",
            ["checks_failed"] = new List<string>()
        };
        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var reviewer = BuildReviewer(agent);

        await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            BuildDiff(),
            BuildImplementerResult(),
            CancellationToken.None);

        var captured = agent.CapturedBrief;
        Assert.NotNull(captured);

        var instruction = captured!.Instruction;

        // Template must state the verifier is read-only with respect to git.
        Assert.Contains("read-only", instruction, StringComparison.OrdinalIgnoreCase);

        // The forbidden git operations must be named explicitly.
        Assert.Contains("git stash", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git checkout", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git reset", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git rebase", instruction, StringComparison.OrdinalIgnoreCase);

        // Template must direct the verifier to read the diff of every changed file before judging.
        Assert.Contains("Read the diff of every changed file", instruction, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 16: Review verdict comes from diff and checks, not git mutation
    // WorkerAgentReviewer receives the pre-built diff and checkResults,
    // passes them to ReviewBriefBuilder, and returns a verdict without
    // making any git call itself (the stub worker never calls git).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task VerifyAsync_VerdictDerivedFromDiffAndChecks_NoGitMutation()
    {
        // Arrange: a diff with one changed file and one failed check result
        var diff = new GitDiff(
            "main",
            "ticket/tlb-99",
            new[]
            {
                new DiffEntry("src/Bar.cs", DiffKind.Modified, null, 5, 2, null)
            });

        var checkResults = new[]
        {
            new CheckResult("build", false, 1, "", "Build failed.", TimeSpan.FromSeconds(1))
        };

        var metadata = new Dictionary<string, object>
        {
            ["verdict"] = "Rework",
            ["rationale"] = "build is broken",
            ["checks_failed"] = new List<string> { "build" }
        };

        var agent = new StubWorkerAgent(OkResultWithMetadata(metadata));
        var options = new WorkerOptions(TimeSpan.FromMinutes(5));
        var reviewer = new WorkerAgentReviewer(
            agent,
            BuildTicket(),
            checkResults,
            options,
            "/repo");

        // Act
        var verdict = await reviewer.VerifyAsync(
            BuildImplementerBrief(),
            diff,
            BuildImplementerResult(),
            CancellationToken.None);

        // Assert: verdict is derived from the injected diff and checkResults
        Assert.Equal(VerdictKind.Rework, verdict.Kind);
        Assert.Equal(new[] { "build" }, verdict.ChecksFailed);

        // The brief instruction received by the worker must reference the changed file and the failed check.
        var instruction = agent.CapturedBrief!.Instruction;
        Assert.Contains("src/Bar.cs", instruction, StringComparison.Ordinal);
        Assert.Contains("FAIL", instruction, StringComparison.Ordinal);
    }
}
