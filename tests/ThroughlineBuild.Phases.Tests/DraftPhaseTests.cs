using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

public class DraftPhaseTests
{
    private static BuildOptions MakeOptions() => new BuildOptions(
        SessionId: "session-draft",
        WorkerName: "claude-code",
        WorkerTimeout: TimeSpan.FromMinutes(5),
        WorkerAllowedTools: null);

    private static DraftPhaseOptions MakeOpts(string text = "Add a widget feature", bool debug = false)
        => new DraftPhaseOptions(text, debug);

    private static IReadOnlyDictionary<string, object> BodyMarkdownMetadata(string body) =>
        new Dictionary<string, object> { ["body_markdown"] = body };

    private const string WellFormedBody =
        "# Add a widget feature\n\nDescription: This ticket adds a widget.\n\n## Acceptance criteria\n\n- [ ] Widget works";

    // ------------------------------------------------------------------
    // Happy path: worker returns well-formed body -> Outcome=Ok
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsOkWithBodyMarkdown()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, BodyMarkdownMetadata(WellFormedBody)));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.Ok, result.Outcome);
        Assert.Equal(WellFormedBody, result.BodyMarkdown);
        Assert.Null(result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Empty input: worker is never invoked
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task RunAsync_EmptyOperatorText_ReturnsEmptyInputWithoutInvokingWorker(string emptyText)
    {
        var worker = new TrackingWorkerAgent();
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(emptyText), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.EmptyInput, result.Outcome);
        Assert.Null(result.BodyMarkdown);
        Assert.NotNull(result.FailureReason);
        Assert.False(worker.WasInvoked, "Worker must not be invoked when operator text is empty.");
    }

    // ------------------------------------------------------------------
    // Worker fails: Outcome=WorkerFailed with reason propagated verbatim
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_WorkerReturnsFailed_ReturnsWorkerFailedWithReason()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Failed, "boom", Array.Empty<string>(), "subprocess crashed",
            new Dictionary<string, object>()));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.WorkerFailed, result.Outcome);
        Assert.Null(result.BodyMarkdown);
        Assert.Equal("subprocess crashed", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WorkerReturnsNeedsRework_ReturnsWorkerFailed()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.NeedsRework, "rework", Array.Empty<string>(), "needs revision",
            new Dictionary<string, object>()));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.WorkerFailed, result.Outcome);
        Assert.Equal("needs revision", result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Worker output missing required sections -> Outcome=InvalidWorkerOutput
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_WorkerOutputMissingTitle_ReturnsInvalidWorkerOutput()
    {
        // No ATX heading and no **Title:** marker
        const string noTitleBody = "Just some text without a heading.\n\nMore text here.";
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, BodyMarkdownMetadata(noTitleBody)));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.InvalidWorkerOutput, result.Outcome);
        Assert.Null(result.BodyMarkdown);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("title", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WorkerOutputMissingDescriptionBody_ReturnsInvalidWorkerOutput()
    {
        // Has a title line but nothing else after it
        const string titleOnlyBody = "# My ticket\n\n   \n\t\n";
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, BodyMarkdownMetadata(titleOnlyBody)));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.InvalidWorkerOutput, result.Outcome);
        Assert.Contains("description", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WorkerMetadataMissingBodyMarkdownKey_ReturnsInvalidWorkerOutput()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null,
            new Dictionary<string, object>())); // empty metadata - no body_markdown
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(), Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(DraftOutcome.InvalidWorkerOutput, result.Outcome);
        Assert.Contains("body_markdown", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Special characters in operator text pass through verbatim to brief
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_OperatorTextWithSpecialChars_PassesThroughVerbatim()
    {
        const string specialText = "newline\nand \"quotes\" and 'apostrophes' and <tags> & more";
        var worker = new CapturingWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, BodyMarkdownMetadata(WellFormedBody)));
        var phase = new DraftPhase(worker, MakeOptions());

        var result = await phase.RunAsync(MakeOpts(specialText), Directory.GetCurrentDirectory(), CancellationToken.None);

        // Phase returned OK
        Assert.Equal(DraftOutcome.Ok, result.Outcome);
        // The instruction passed to the worker contains the special chars verbatim
        Assert.NotNull(worker.CapturedBrief);
        Assert.Contains(specialText, worker.CapturedBrief!.Instruction);
    }

    // ------------------------------------------------------------------
    // IWorkflowPhase explicit implementation throws NotSupportedException
    // ------------------------------------------------------------------

    [Fact]
    public async Task IWorkflowPhase_RunAsync_ThrowsNotSupportedException()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, new Dictionary<string, object>()));
        IWorkflowPhase phase = new DraftPhase(worker, MakeOptions());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => phase.RunAsync("TLB-1", Directory.GetCurrentDirectory(), CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Phase property
    // ------------------------------------------------------------------

    [Fact]
    public void Phase_ReturnsDraft()
    {
        var worker = new FakeWorkerAgent(new WorkerResult(
            Status.Ok, "done", Array.Empty<string>(), null, new Dictionary<string, object>()));
        var phase = new DraftPhase(worker, MakeOptions());

        Assert.Equal(Phase.Draft, phase.Phase);
    }

    // ------------------------------------------------------------------
    // Private fakes
    // ------------------------------------------------------------------

    private sealed class FakeWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public FakeWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    /// <summary>Records whether ExecuteAsync was called at all.</summary>
    private sealed class TrackingWorkerAgent : IWorkerAgent
    {
        public bool WasInvoked { get; private set; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(new WorkerResult(Status.Ok, "ok", Array.Empty<string>(), null, new Dictionary<string, object>()));
        }
    }

    /// <summary>Captures the Brief passed to ExecuteAsync.</summary>
    private sealed class CapturingWorkerAgent : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public Brief? CapturedBrief { get; private set; }
        public CapturingWorkerAgent(WorkerResult result) { _result = result; }
        public string Name => "claude-code";
        public IWorkerProgressDigester? Digester => null;
        public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            CapturedBrief = brief;
            return Task.FromResult(_result);
        }
    }
}
