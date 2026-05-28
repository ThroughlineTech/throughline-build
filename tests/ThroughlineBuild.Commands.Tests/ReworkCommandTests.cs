using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

/// <summary>
/// Unit tests for ReworkCommand: output line ordering, per-outcome error blocks,
/// --feedback and --debug forwarding.
/// </summary>
[Collection("CommandConsoleTests")]
public class ReworkCommandTests
{
    private const string TicketId = "TLB-147";

    private static TicketCommandContext MakeCtx(
        string ticketId = TicketId,
        string? feedback = null,
        bool debug = false)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["debug"] = debug ? "true" : "false"
        };
        if (feedback is not null)
            args["feedback"] = feedback;
        return new TicketCommandContext(ticketId, args);
    }

    private static (ReworkCommand cmd, FakeReworkRunner runner) BuildCommand()
    {
        var runner = new FakeReworkRunner();
        var cmd = new ReworkCommand(runner, "/fake/cwd");
        return (cmd, runner);
    }

    // Captures Console.Out and returns all written text.
    private static async Task<(CommandResult result, string output)> RunCapturingStdout(
        ReworkCommand cmd,
        TicketCommandContext ctx)
    {
        var originalOut = Console.Out;
        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // --- happy path ---

    [Fact]
    public async Task HappyPath_outputs_expected_lines_and_succeeds()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", "ticket/tlb-147", "/worktrees/tlb-147", null, ReworkRoundNumber: 1),
            FailureReason: null,
            FeedbackSource: "event-log");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.True(result.Success);
        Assert.Contains($"[{TicketId}] rework starting (feedback source: event-log)", output);
        Assert.Contains($"[{TicketId}] reviewer rationale:", output);
        Assert.Contains($"[{TicketId}] checks failed:", output);
        Assert.Contains($"[{TicketId}] implement (rework round 1):", output);
        Assert.Contains($"[{TicketId}] state: InProgress -> InReview", output);
        Assert.Contains($"[{TicketId}] rework complete; run `build review 147` to re-verdict", output);
    }

    [Fact]
    public async Task HappyPath_stores_result_for_exit_code_mapping()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", "ticket/tlb-147", null, null, 1),
            FailureReason: null,
            FeedbackSource: "event-log");

        await RunCapturingStdout(cmd, MakeCtx());

        Assert.NotNull(cmd.LastReworkResult);
        Assert.Equal(ReworkOutcome.Implemented, cmd.LastReworkResult!.Outcome);
    }

    // --- --feedback path ---

    [Fact]
    public async Task FeedbackFlag_shows_manual_source_and_passes_feedback_to_runner()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", "ticket/tlb-147", null, null, 1),
            FailureReason: null,
            FeedbackSource: "manual");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx(feedback: "tests are red"));

        Assert.True(result.Success);
        Assert.Contains("feedback source: manual", output);
        Assert.Equal("tests are red", runner.LastOptions?.ManualFeedback);
    }

    [Fact]
    public async Task FeedbackFlag_rationale_appears_in_success_output()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", "ticket/tlb-147", null, null, 1),
            FailureReason: null,
            FeedbackSource: "manual");

        var (_, output) = await RunCapturingStdout(cmd, MakeCtx(feedback: "specific feedback text"));

        // The manual feedback text should appear in the rationale line.
        Assert.Contains("specific feedback text", output);
    }

    // --- --debug forwarded ---

    [Fact]
    public async Task Debug_flag_forwarded_to_runner()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", null, null, null, 1),
            FailureReason: null,
            FeedbackSource: "event-log");

        await RunCapturingStdout(cmd, MakeCtx(debug: true));

        Assert.True(runner.LastOptions?.Debug, "expected debug=true to be forwarded");
    }

    [Fact]
    public async Task Debug_false_forwarded_when_not_set()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.Implemented,
            ImplementResult: new ImplementResult(true, TicketId, "abc123", null, null, null, 1),
            FailureReason: null,
            FeedbackSource: "event-log");

        await RunCapturingStdout(cmd, MakeCtx(debug: false));

        Assert.False(runner.LastOptions?.Debug, "expected debug=false to be forwarded");
    }

    // --- NoFeedbackAvailable outcome ---

    [Fact]
    public async Task NoFeedbackAvailable_outputs_error_block_and_fails()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.NoFeedbackAvailable,
            ImplementResult: null,
            FailureReason: "no Rework verdict found in event log for ticket TLB-147",
            FeedbackSource: "");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("no Rework verdict found in event log", output);
        Assert.Contains("supply feedback manually with --feedback", output);
        Assert.Equal(ReworkOutcome.NoFeedbackAvailable, cmd.LastReworkResult?.Outcome);
    }

    // --- TicketNotInProgress outcome ---

    [Fact]
    public async Task TicketNotInProgress_outputs_error_block_and_fails()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.TicketNotInProgress,
            ImplementResult: null,
            FailureReason: "ticket is in Done; if Rework was the verdict, this is unexpected",
            FeedbackSource: "");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("ticket is in Done, not InProgress", output);
        Assert.Contains("Current state: Done", output);
        Assert.Contains("build review 147", output);
        Assert.Equal(ReworkOutcome.TicketNotInProgress, cmd.LastReworkResult?.Outcome);
    }

    // --- ImplementFailed outcome ---

    [Fact]
    public async Task ImplementFailed_surfaces_failure_reason()
    {
        var (cmd, runner) = BuildCommand();
        runner.Result = new ReworkResult(
            TicketId: TicketId,
            Outcome: ReworkOutcome.ImplementFailed,
            ImplementResult: new ImplementResult(false, TicketId, null, null, null, "compile error: CS0001", 1),
            FailureReason: "compile error: CS0001",
            FeedbackSource: "event-log");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("rework failed: implement phase failed", output);
        Assert.Contains("compile error: CS0001", output);
        Assert.Equal(ReworkOutcome.ImplementFailed, cmd.LastReworkResult?.Outcome);
    }

    // --- empty ticket-id ---

    [Fact]
    public async Task Empty_ticketId_returns_failure_without_calling_runner()
    {
        var (cmd, runner) = BuildCommand();
        var ctx = new TicketCommandContext("", new Dictionary<string, string>());

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ticket-id is required", result.Message ?? "");
        Assert.Equal(0, runner.CallCount); // runner must not be called
    }
}

/// <summary>
/// Test double for IReworkRunner.
/// Returns a configured ReworkResult and records the options passed in.
/// </summary>
internal sealed class FakeReworkRunner : IReworkRunner
{
    public ReworkResult Result { get; set; } = new ReworkResult(
        TicketId: "TLB-147",
        Outcome: ReworkOutcome.Implemented,
        ImplementResult: null,
        FailureReason: null,
        FeedbackSource: "event-log");

    public ReworkPhaseOptions? LastOptions { get; private set; }
    public string? LastTicketId { get; private set; }
    public int CallCount { get; private set; }

    public Task<ReworkResult> RunAsync(
        string ticketId,
        string workingDirectory,
        ReworkPhaseOptions options,
        CancellationToken ct)
    {
        LastTicketId = ticketId;
        LastOptions = options;
        CallCount++;
        return Task.FromResult(Result);
    }
}
