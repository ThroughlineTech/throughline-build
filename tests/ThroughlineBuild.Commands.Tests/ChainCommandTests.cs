using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Commands.Tests;

/// <summary>
/// Unit tests for ChainCommand: output line ordering, per-outcome operator-triage text,
/// rework-cap-exceeded rationale/checks section, and --debug forwarding.
/// </summary>
[Collection("CommandConsoleTests")]
public class ChainCommandTests
{
    private static Ticket MakeTicket(TicketState state = TicketState.Ready) =>
        new Ticket(
            Id: "TLB-1",
            Uuid: "test-uuid-1",
            Title: "Test ticket",
            Type: "feature",
            State: state,
            Size: Size.S,
            Risk: Risk.Low,
            DescriptionHtml: "<p>desc</p>",
            Relations: Array.Empty<Relation>(),
            Labels: Array.Empty<string>(),
            ParentId: null);

    private static TicketCommandContext MakeCtx(
        string ticketId = "TLB-1",
        bool debug = false) =>
        new TicketCommandContext(
            ticketId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["debug"] = debug ? "true" : "false"
            });

    private static (ChainCommand cmd, FakeChainRunner runner, FakeTicketing ticketing)
        BuildCommand(Ticket? ticket = null)
    {
        var t = ticket ?? MakeTicket();
        var ticketing = new FakeTicketing(t);
        var runner = new FakeChainRunner();
        var cmd = new ChainCommand(runner, ticketing);
        return (cmd, runner, ticketing);
    }

    // Captures Console.Out and returns all written lines.
    private static async Task<(CommandResult result, string output)> RunCapturingStdout(
        ChainCommand cmd,
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
    public async Task HappyPath_steps_printed_before_final_line()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("plan",    -1, Status.Ok, null, null,            TimeSpan.FromSeconds(2), null),
                new ChainStep("implement", 0, Status.Ok, null, null,           TimeSpan.FromSeconds(5), null),
                new ChainStep("review",  -1, Status.Ok, null, VerdictKind.Pass, TimeSpan.FromSeconds(3), null),
                new ChainStep("ship",    -1, Status.Ok, null, null,            TimeSpan.FromSeconds(1), null),
            },
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.FromSeconds(11),
            FinalRationale: null);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.True(result.Success);

        // Step lines are emitted by the onStep callback BEFORE RunAsync returns.
        // The final "chain complete" line is printed after RunAsync returns.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var planIdx    = Array.FindIndex(lines, l => l.Contains("[TLB-1] plan"));
        var implIdx    = Array.FindIndex(lines, l => l.Contains("[TLB-1] implement"));
        var reviewIdx  = Array.FindIndex(lines, l => l.Contains("[TLB-1] review"));
        var shipIdx    = Array.FindIndex(lines, l => l.Contains("[TLB-1] ship") && !l.Contains("chain"));
        var completeIdx = Array.FindIndex(lines, l => l.Contains("chain complete"));

        Assert.True(planIdx >= 0,    "expected plan step line");
        Assert.True(implIdx >= 0,    "expected implement step line");
        Assert.True(reviewIdx >= 0,  "expected review step line");
        Assert.True(shipIdx >= 0,    "expected ship step line");
        Assert.True(completeIdx >= 0, "expected chain complete line");

        // All step lines must appear before the final line.
        Assert.True(planIdx    < completeIdx);
        Assert.True(implIdx    < completeIdx);
        Assert.True(reviewIdx  < completeIdx);
        Assert.True(shipIdx    < completeIdx);
    }

    [Fact]
    public async Task HappyPath_returns_success()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.FromSeconds(1),
            FinalRationale: null);

        var (result, _) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.True(result.Success);
    }

    // --- step line format ---

    [Fact]
    public void FormatStepLine_ok_status_no_reason()
    {
        var step = new ChainStep("plan", -1, Status.Ok, null, null, TimeSpan.FromSeconds(5), null);
        var line = ChainCommand.FormatStepLine("TLB-1", step);
        Assert.Contains("[TLB-1] plan", line);
        Assert.Contains("Ok", line);
        Assert.Contains("5s", line);
    }

    [Fact]
    public void FormatStepLine_failed_status_includes_reason()
    {
        var step = new ChainStep("implement", 0, Status.Failed, "build error", null, TimeSpan.FromSeconds(3), null);
        var line = ChainCommand.FormatStepLine("TLB-1", step);
        Assert.Contains("[TLB-1] implement", line);
        Assert.Contains("Failed", line);
        Assert.Contains("build error", line);
    }

    [Fact]
    public void FormatStepLine_review_with_verdict_shows_verdict()
    {
        var step = new ChainStep("review", -1, Status.Ok, null, VerdictKind.Pass, TimeSpan.FromSeconds(8), null);
        var line = ChainCommand.FormatStepLine("TLB-1", step);
        Assert.Contains("[TLB-1] review", line);
        Assert.Contains("Pass", line);
        Assert.DoesNotContain("Ok", line);
    }

    [Fact]
    public void FormatStepLine_rework_round_includes_round_number()
    {
        var step = new ChainStep("implement", 1, Status.Ok, null, null, TimeSpan.FromSeconds(4), null);
        var line = ChainCommand.FormatStepLine("TLB-1", step);
        Assert.Contains("round 1", line);
    }

    // --- outcome: RefusedInitialState ---

    [Fact]
    public async Task RefusedInitialState_returns_failure_with_triage()
    {
        var (cmd, runner, _) = BuildCommand(MakeTicket(TicketState.Done));
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.RefusedInitialState,
            TotalDuration: TimeSpan.Zero,
            FinalRationale: null);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("chain stopped", output);
        Assert.Contains("Operator triage", output);
    }

    // --- outcome: StoppedAtPlan ---

    [Fact]
    public async Task StoppedAtPlan_returns_failure_with_triage()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("plan", -1, Status.Failed, "llm timeout", null, TimeSpan.FromSeconds(1), null)
            },
            Outcome: ChainOutcome.StoppedAtPlan,
            TotalDuration: TimeSpan.FromSeconds(1),
            FinalRationale: null);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("planning failed", output);
        Assert.Contains("Operator triage", output);
        Assert.Contains("build plan TLB-1", output);
    }

    // --- outcome: StoppedAtImplement ---

    [Fact]
    public async Task StoppedAtImplement_returns_failure_with_triage()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("implement", 0, Status.Failed, "compile error", null, TimeSpan.FromSeconds(10), null)
            },
            Outcome: ChainOutcome.StoppedAtImplement,
            TotalDuration: TimeSpan.FromSeconds(10),
            FinalRationale: null);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("implementation failed", output);
        Assert.Contains("Operator triage", output);
    }

    // --- outcome: StoppedAtReview ---

    [Fact]
    public async Task StoppedAtReview_returns_failure_with_triage()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("implement", 0, Status.Ok,   null,            null, TimeSpan.FromSeconds(10), null),
                new ChainStep("review",   -1, Status.Ok,   null, VerdictKind.Fail, TimeSpan.FromSeconds(5), null),
            },
            Outcome: ChainOutcome.StoppedAtReview,
            TotalDuration: TimeSpan.FromSeconds(15),
            FinalRationale: "requirements not met");

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("review returned Fail", output);
        Assert.Contains("Operator triage", output);
        // FinalRationale should appear in the output.
        Assert.Contains("requirements not met", output);
    }

    // --- outcome: ReworkCapExceeded ---

    [Fact]
    public async Task ReworkCapExceeded_returns_failure_with_checks_section()
    {
        var (cmd, runner, _) = BuildCommand();
        var rationale = "Checks failed:\n- test suite red\n- missing acceptance item\n";
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("implement", 0, Status.Ok, null, null,              TimeSpan.FromSeconds(10), null),
                new ChainStep("review",   -1, Status.Ok, null, VerdictKind.Rework, TimeSpan.FromSeconds(5), null),
                new ChainStep("implement", 1, Status.Ok, null, null,              TimeSpan.FromSeconds(10), null),
                new ChainStep("review",   -1, Status.Ok, null, VerdictKind.Rework, TimeSpan.FromSeconds(5), null),
                new ChainStep("implement", 2, Status.Ok, null, null,              TimeSpan.FromSeconds(10), null),
                new ChainStep("review",   -1, Status.Ok, null, VerdictKind.Rework, TimeSpan.FromSeconds(5), null),
            },
            Outcome: ChainOutcome.ReworkCapExceeded,
            TotalDuration: TimeSpan.FromSeconds(45),
            FinalRationale: rationale);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("rework cap exceeded", output);
        Assert.Contains("Operator triage", output);
        Assert.Contains("Checks failed:", output);
    }

    // --- outcome: StoppedAtShip ---

    [Fact]
    public async Task StoppedAtShip_returns_failure_with_triage()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: new[]
            {
                new ChainStep("implement", 0, Status.Ok, null, null,             TimeSpan.FromSeconds(10), null),
                new ChainStep("review",   -1, Status.Ok, null, VerdictKind.Pass, TimeSpan.FromSeconds(5), null),
                new ChainStep("ship",     -1, Status.Failed, "merge conflict",   null, TimeSpan.FromSeconds(2), null),
            },
            Outcome: ChainOutcome.StoppedAtShip,
            TotalDuration: TimeSpan.FromSeconds(17),
            FinalRationale: null);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("ship gate failed", output);
        Assert.Contains("Operator triage", output);
        Assert.Contains("build ship TLB-1", output);
    }

    // --- debug flag forwarding ---

    [Fact]
    public async Task Debug_flag_forwarded_to_runner()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.Zero,
            FinalRationale: null);

        await RunCapturingStdout(cmd, MakeCtx(debug: true));

        Assert.True(runner.LastDebug, "expected debug=true to be forwarded to runner");
    }

    [Fact]
    public async Task Debug_false_forwarded_to_runner_when_not_set()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.Zero,
            FinalRationale: null);

        await RunCapturingStdout(cmd, MakeCtx(debug: false));

        Assert.False(runner.LastDebug, "expected debug=false to be forwarded to runner");
    }

    // --- outcome: ParentCompleted ---

    [Fact]
    public async Task ParentCompleted_returns_success_with_child_summaries()
    {
        var (cmd, runner, _) = BuildCommand();
        var childResults = new[]
        {
            new ChainResult("TLB-2", Array.Empty<ChainStep>(), ChainOutcome.Completed,
                TimeSpan.FromSeconds(10), null),
            new ChainResult("TLB-3", Array.Empty<ChainStep>(), ChainOutcome.Completed,
                TimeSpan.FromSeconds(8), null),
        };
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.ParentCompleted,
            TotalDuration: TimeSpan.FromSeconds(20),
            FinalRationale: "All 2 eligible children completed.",
            ChildResults: childResults);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.True(result.Success);
        Assert.Contains("parent chain complete", output);
        Assert.Contains("TLB-2", output);
        Assert.Contains("TLB-3", output);
    }

    // --- outcome: ParentStoppedEarly ---

    [Fact]
    public async Task ParentStoppedEarly_returns_failure_with_child_summaries()
    {
        var (cmd, runner, _) = BuildCommand();
        var childResults = new[]
        {
            new ChainResult("TLB-2", Array.Empty<ChainStep>(), ChainOutcome.Completed,
                TimeSpan.FromSeconds(10), null),
            new ChainResult("TLB-3", Array.Empty<ChainStep>(), ChainOutcome.StoppedAtPlan,
                TimeSpan.FromSeconds(3), null),
        };
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.ParentStoppedEarly,
            TotalDuration: TimeSpan.FromSeconds(15),
            FinalRationale: "One or more children did not complete: TLB-3",
            ChildResults: childResults);

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.False(result.Success);
        Assert.Contains("parent chain stopped early", output);
        Assert.Contains("TLB-2", output);
        Assert.Contains("TLB-3", output);
    }

    // --- outcome: RatifiedObsolete ---

    [Fact]
    public async Task RatifiedObsolete_final_line_contains_subsumed_commit()
    {
        var (cmd, runner, _) = BuildCommand();
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: Array.Empty<ChainStep>(),
            Outcome: ChainOutcome.RatifiedObsolete,
            TotalDuration: TimeSpan.FromSeconds(5),
            FinalRationale: "Subsumed by abc123: prior work satisfies acceptance criteria; files: src/Foo.cs",
            SubsumedBy: new SubsumedByEvidence("abc123", new[] { "src/Foo.cs" }, "prior work satisfies acceptance criteria"));

        var (result, output) = await RunCapturingStdout(cmd, MakeCtx());

        Assert.True(result.Success);
        Assert.Contains("Subsumed by abc123", output);
        Assert.DoesNotContain("Failed", output);
    }

    // --- onStep streaming ---

    [Fact]
    public async Task OnStep_called_for_each_step_during_run()
    {
        // Verifies that the onStep lambda is invoked by the runner (FakeChainRunner
        // calls it synchronously for each step before returning the result).
        var (cmd, runner, _) = BuildCommand();
        var steps = new[]
        {
            new ChainStep("plan",      -1, Status.Ok, null, null,            TimeSpan.FromSeconds(1), null),
            new ChainStep("implement",  0, Status.Ok, null, null,            TimeSpan.FromSeconds(2), null),
            new ChainStep("review",    -1, Status.Ok, null, VerdictKind.Pass, TimeSpan.FromSeconds(1), null),
        };
        runner.Result = new ChainResult(
            TicketId: "TLB-1",
            Steps: steps,
            Outcome: ChainOutcome.Completed,
            TotalDuration: TimeSpan.FromSeconds(4),
            FinalRationale: null);

        var (_, output) = await RunCapturingStdout(cmd, MakeCtx());

        // Each step line was printed via the onStep callback.
        Assert.Contains("[TLB-1] plan", output);
        Assert.Contains("[TLB-1] implement", output);
        Assert.Contains("[TLB-1] review", output);
    }

    // --- empty ticket-id ---

    [Fact]
    public async Task Empty_ticketId_returns_failure()
    {
        var (cmd, _, _) = BuildCommand();
        var ctx = new TicketCommandContext("", new Dictionary<string, string>());

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ticket-id is required", result.Message ?? "");
    }

    // --- unhandled exception from runner ---

    [Fact]
    public async Task RunnerException_returns_failure_with_message_and_null_LastChainResult()
    {
        // When the runner throws an unexpected exception (e.g., NotSupportedException
        // from AOT serialization of an unregistered type), ChainCommand should catch it,
        // return failure, and populate CommandResult.Message so Program.cs can print it.
        var t = MakeTicket();
        var ticketing = new FakeTicketing(t);
        var runner = new FakeThrowingChainRunner(new InvalidOperationException("chain failed internally"));
        var cmd = new ChainCommand(runner, ticketing);
        var ctx = MakeCtx();

        var result = await cmd.ExecuteAsync(ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("chain failed", result.Message ?? "");
        Assert.Contains("chain failed internally", result.Message ?? "");
        Assert.Null(cmd.LastChainResult);
    }
}

/// <summary>
/// Test-double IChainRunner that throws on RunAsync to exercise the exception path.
/// </summary>
internal sealed class FakeThrowingChainRunner : IChainRunner
{
    private readonly Exception _exception;

    public FakeThrowingChainRunner(Exception exception) => _exception = exception;

    public Task<ChainResult> RunAsync(string ticketId, bool debug, Action<ChainStep> onStep, CancellationToken ct, bool noAutoResolve = false)
        => throw _exception;
}

/// <summary>
/// Test-double IChainRunner. Invokes the onStep callback for each step in the
/// configured Result before returning, simulating per-step streaming.
/// </summary>
internal sealed class FakeChainRunner : IChainRunner
{
    public ChainResult Result { get; set; } = new ChainResult(
        TicketId: "TLB-1",
        Steps: Array.Empty<ChainStep>(),
        Outcome: ChainOutcome.Completed,
        TotalDuration: TimeSpan.Zero,
        FinalRationale: null);

    public bool LastDebug { get; private set; }
    public string? LastTicketId { get; private set; }

    public Task<ChainResult> RunAsync(
        string ticketId,
        bool debug,
        Action<ChainStep> onStep,
        CancellationToken ct,
        bool noAutoResolve = false)
    {
        LastTicketId = ticketId;
        LastDebug = debug;

        // Call onStep for each step to simulate streaming behavior.
        foreach (var step in Result.Steps)
            onStep(step);

        return Task.FromResult(Result);
    }
}
