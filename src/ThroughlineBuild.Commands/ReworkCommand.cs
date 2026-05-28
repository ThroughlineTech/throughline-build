using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Implements the "build rework &lt;ticket-id&gt;" command.
/// Invokes ReworkPhase end-to-end and streams a small fixed set of output lines
/// to stdout. Exposes LastReworkResult so Program.cs can map the outcome to an exit code.
/// </summary>
public sealed class ReworkCommand : ITicketCommand
{
    private readonly IReworkRunner _runner;
    private readonly string _workingDirectory;

    public ReworkCommand(IReworkRunner runner, string workingDirectory)
    {
        _runner = runner;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Gets the last ReworkResult from execution (used by Program.cs to map exit codes).
    /// </summary>
    public ReworkResult? LastReworkResult { get; private set; }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        var ticketId = ctx.TicketId;
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return new CommandResult(false, "ticket-id is required");
        }

        // Extract optional --feedback and --debug from args.
        ctx.Args.TryGetValue("feedback", out var manualFeedback);
        bool debugMode = ctx.Args.TryGetValue("debug", out var debugStr) && debugStr == "true";

        // Derive feedback source label before phase runs so it can be printed immediately.
        var feedbackSourceLabel = manualFeedback is not null ? "manual" : "event-log";

        Console.WriteLine($"[{ticketId}] rework starting (feedback source: {feedbackSourceLabel})");

        var options = new ReworkPhaseOptions(
            TicketId: ticketId,
            ManualFeedback: manualFeedback,
            ReworkRoundNumber: 1,
            Debug: debugMode);

        ReworkResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            result = await _runner.RunAsync(ticketId, _workingDirectory, options, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "cancelled");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"rework failed: {ex.Message}");
        }
        sw.Stop();

        LastReworkResult = result;

        switch (result.Outcome)
        {
            case ReworkOutcome.Implemented:
                // Build a manual-feedback ReviewFeedback for display purposes when --feedback was supplied.
                // When the phase retrieved feedback from the event log, we don't have it here, so we
                // use empty defaults; the key output lines (round number, duration) are still correct.
                var displayFeedback = manualFeedback is not null
                    ? new ReviewFeedback(manualFeedback, Array.Empty<string>(), 1)
                    : null;
                PrintSuccessOutput(ticketId, result, sw.Elapsed, displayFeedback);
                return new CommandResult(true, string.Empty);

            case ReworkOutcome.NoFeedbackAvailable:
                PrintNoFeedbackAvailableOutput(ticketId);
                return new CommandResult(false, string.Empty);

            case ReworkOutcome.TicketNotInProgress:
                PrintTicketNotInProgressOutput(ticketId, result.FailureReason ?? "Unknown");
                return new CommandResult(false, string.Empty);

            case ReworkOutcome.ImplementFailed:
                PrintImplementFailedOutput(ticketId, result.FailureReason ?? "unknown failure");
                return new CommandResult(false, string.Empty);

            default:
                Console.Error.WriteLine($"[{ticketId}] rework failed: unexpected outcome {result.Outcome}");
                return new CommandResult(false, $"unexpected outcome: {result.Outcome}");
        }
    }

    private static void PrintSuccessOutput(
        string ticketId,
        ReworkResult result,
        TimeSpan elapsed,
        ReviewFeedback? displayFeedback)
    {
        var rationale = displayFeedback?.Rationale ?? string.Empty;
        var rationaleSnippet = rationale.Length > 200
            ? rationale.Substring(0, 200) + "..."
            : rationale;
        Console.WriteLine($"[{ticketId}] reviewer rationale: {rationaleSnippet}");

        var checkCount = displayFeedback?.ChecksFailed?.Count ?? 0;
        Console.WriteLine($"[{ticketId}] checks failed: {checkCount}");

        var durationStr = FormatDuration(elapsed);
        var roundNumber = result.ImplementResult?.ReworkRoundNumber ?? 1;
        Console.WriteLine($"[{ticketId}] implement (rework round {roundNumber}): Ok ({durationStr})");

        Console.WriteLine($"[{ticketId}] state: InProgress -> InReview");

        var shortId = ticketId.Contains('-') ? ticketId.Split('-')[^1] : ticketId;
        Console.WriteLine($"[{ticketId}] rework complete; run `build review {shortId}` to re-verdict");
    }

    private static void PrintNoFeedbackAvailableOutput(string ticketId)
    {
        var shortId = ticketId.Contains('-') ? ticketId.Split('-')[^1] : ticketId;
        Console.WriteLine($"[{ticketId}] rework failed: no Rework verdict found in event log");
        Console.WriteLine();
        Console.WriteLine($"The event log at .build/events/ contains no Review event for {ticketId} with Verdict=Rework.");
        Console.WriteLine("This can happen if:");
        Console.WriteLine("  - The review was run on a different machine");
        Console.WriteLine("  - The events directory was cleared");
        Console.WriteLine("  - The most recent review verdict was Pass or Fail (not Rework)");
        Console.WriteLine();
        Console.WriteLine($"To proceed: supply feedback manually with --feedback \"...\"");
        Console.WriteLine($"To check: review the .build/events/*.jsonl files or re-run `build review {shortId}`");
    }

    private static void PrintTicketNotInProgressOutput(string ticketId, string failureReason)
    {
        var stateName = ExtractStateName(failureReason);
        var shortId = ticketId.Contains('-') ? ticketId.Split('-')[^1] : ticketId;
        Console.WriteLine($"[{ticketId}] rework failed: ticket is in {stateName}, not InProgress");
        Console.WriteLine();
        Console.WriteLine("Rework requires a ticket in InProgress state (where a Rework verdict transitions it).");
        Console.WriteLine($"Current state: {stateName}");
        Console.WriteLine();
        Console.WriteLine($"If the ticket is in InReview, run `build review {shortId}` to verdict it first.");
        Console.WriteLine("If the ticket is in Done, the work is complete - no rework needed.");
    }

    private static void PrintImplementFailedOutput(string ticketId, string failureReason)
    {
        Console.WriteLine($"[{ticketId}] rework failed: implement phase failed");
        Console.WriteLine();
        Console.WriteLine($"Failure reason: {failureReason}");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  - Inspect the failure reason above and resolve manually.");
        Console.WriteLine($"  - Re-run `build rework {ticketId}` after addressing the issue.");
        Console.WriteLine($"  - Supply --feedback \"...\" to override the event-log feedback.");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (long)duration.TotalSeconds;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }

    private static string ExtractStateName(string failureReason)
    {
        // ReworkPhase sets: "ticket is in {ticket.State}; if Rework was the verdict..."
        // Extract the state name between "ticket is in " and the next ";"
        const string prefix = "ticket is in ";
        var idx = failureReason.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return "Unknown";
        var start = idx + prefix.Length;
        var end = failureReason.IndexOf(';', start);
        return end >= 0
            ? failureReason.Substring(start, end - start).Trim()
            : failureReason.Substring(start).Trim();
    }
}
