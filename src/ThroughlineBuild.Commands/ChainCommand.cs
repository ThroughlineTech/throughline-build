using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Implements the "build chain &lt;ticket-id&gt;" command.
/// Uses IChainRunner to stream per-phase output to stdout as each phase completes,
/// then prints a final result summary with operator-triage suggestions.
/// </summary>
public sealed class ChainCommand : ITicketCommand
{
    private readonly IChainRunner _runner;
    private readonly ITicketing _ticketing;

    public ChainCommand(IChainRunner runner, ITicketing ticketing)
    {
        _runner = runner;
        _ticketing = ticketing;
    }

    /// <summary>
    /// Gets the last ChainResult from execution (used by Program.cs to map exit codes).
    /// </summary>
    public ChainResult? LastChainResult { get; private set; }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        var ticketId = ctx.TicketId;
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return new CommandResult(false, "ticket-id is required");
        }

        // Extract --debug flag from args.
        bool debugMode = ctx.Args.TryGetValue("debug", out var debugStr) && debugStr == "true";

        // Extract --no-auto-resolve flag from args.
        bool noAutoResolve = ctx.Args.TryGetValue("no-auto-resolve", out var narStr) && narStr == "true";

        Ticket? initialTicket = null;
        try
        {
            initialTicket = await _ticketing.GetAsync(ticketId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"failed to fetch ticket: {ex.Message}");
        }

        if (initialTicket == null)
        {
            return new CommandResult(false, $"ticket not found: {ticketId}");
        }

        // Write "chain starting" header with initial state.
        Console.WriteLine($"[{ticketId}] chain starting (initial state: {initialTicket.State})");

        ChainResult result;
        try
        {
            // Use the ct passed in from Program.cs - no duplicate CancelKeyPress registration.
            result = await _runner.RunAsync(
                ticketId,
                debugMode,
                (id, step) => Console.WriteLine(FormatStepLine(id, step)),
                ct,
                noAutoResolve).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "cancelled");
        }
        catch (KeyNotFoundException ex)
        {
            return new CommandResult(false, $"ticket not found: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, $"chain failed: {ex.Message}");
        }

        // Store result for exit code mapping.
        LastChainResult = result;

        // Write final chain completion/stop line and operator triage.
        var finalLine = FormatFinalLine(ticketId, result);
        Console.WriteLine(finalLine);

        // For parent-chain results, print per-child summary lines.
        if (result.ChildResults is { Count: > 0 })
        {
            foreach (var child in result.ChildResults)
            {
                Console.WriteLine(FormatChildSummaryLine(child));
            }
        }

        // Emit operator triage suggestions if the chain did not complete.
        if (result.Outcome != ChainOutcome.Completed &&
            result.Outcome != ChainOutcome.RatifiedObsolete &&
            result.Outcome != ChainOutcome.ParentCompleted)
        {
            var triage = GetOperatorTriageSuggestions(ticketId, result, initialTicket);
            if (!string.IsNullOrEmpty(triage))
            {
                Console.WriteLine();
                Console.WriteLine(triage);
            }
        }

        // Return success for Completed, RatifiedObsolete, and ParentCompleted.
        bool success = result.Outcome == ChainOutcome.Completed
            || result.Outcome == ChainOutcome.RatifiedObsolete
            || result.Outcome == ChainOutcome.ParentCompleted;
        return new CommandResult(success, string.Empty);
    }

    public static string FormatStepLine(string ticketId, ChainStep step)
    {
        var statusStr = step.Status switch
        {
            Status.Ok => "Ok",
            Status.Failed => $"Failed - {step.FailureReason ?? "unknown"}",
            Status.NeedsRework => "Rework",
            _ => step.Status.ToString()
        };

        var roundStr = step.ReworkRoundNumber >= 0 ? $" (round {step.ReworkRoundNumber})" : "";
        var durationStr = FormatDuration(step.Duration);

        // If step has a verdict (from review phase), format with verdict instead of status.
        if (step.Verdict.HasValue)
        {
            return $"[{ticketId}] {step.PhaseName}: {step.Verdict} ({durationStr})";
        }

        return $"[{ticketId}] {step.PhaseName}{roundStr}: {statusStr} ({durationStr})";
    }

    private static string FormatFinalLine(string ticketId, ChainResult result)
    {
        var durationStr = FormatDuration(result.TotalDuration);

        return result.Outcome switch
        {
            ChainOutcome.Completed =>
                $"[{ticketId}] chain complete ({durationStr})",

            ChainOutcome.RatifiedObsolete =>
                $"[{ticketId}] Subsumed by {result.SubsumedBy?.Commit ?? "(unknown)"} - continuing ({durationStr})",

            ChainOutcome.RefusedInitialState =>
                $"[{ticketId}] chain stopped: initial state does not allow chain execution",

            ChainOutcome.RefusedDirtyTree =>
                $"[{ticketId}] chain refused: working tree not clean ({result.FinalRationale})",

            ChainOutcome.StoppedAtPlan =>
                $"[{ticketId}] chain stopped: planning failed",

            ChainOutcome.StoppedAtImplement =>
                $"[{ticketId}] chain stopped: implementation failed",

            ChainOutcome.StoppedAtReview =>
                $"[{ticketId}] chain stopped: review returned Fail",

            ChainOutcome.ReworkCapExceeded =>
                $"[{ticketId}] chain stopped: rework cap exceeded after {CountImplementRounds(result.Steps)} implement attempts",

            ChainOutcome.StoppedAtShip =>
                $"[{ticketId}] chain stopped: ship gate failed",

            ChainOutcome.ParentCompleted =>
                $"[{ticketId}] parent chain complete: all eligible children completed ({durationStr})",

            ChainOutcome.ParentStoppedEarly =>
                $"[{ticketId}] parent chain stopped early: one or more children did not complete ({durationStr})",

            ChainOutcome.ParentHasGrandchildren =>
                $"[{ticketId}] chain stopped: tree is deeper than one level - chain the intermediate ticket(s) directly",

            ChainOutcome.Skipped =>
                $"[{ticketId}] skipped ({durationStr}){(result.SkipReason is not null ? " - " + result.SkipReason : "")}",

            _ => $"[{ticketId}] chain stopped: unknown outcome {result.Outcome}"
        };
    }

    /// <summary>
    /// Prints an aggregate report for a multi-ticket dispatch run.
    /// Format:
    ///   --- aggregate report ---
    ///   [TLB-A] Completed (2.1s)
    ///   [TLB-B] Failed (0.5s) - reason here
    ///   [TLB-C] Skipped (0.0s) - skipped (ancestor TLB-B failed)
    ///   3 tickets: 1 completed, 1 failed, 1 skipped
    /// </summary>
    public static void PrintAggregateReport(IReadOnlyList<ChainResult> results)
    {
        Console.WriteLine("--- aggregate report ---");

        int completed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (var r in results)
        {
            var durationStr = FormatDuration(r.TotalDuration);
            bool isSuccess = r.Outcome == ChainOutcome.Completed
                || r.Outcome == ChainOutcome.RatifiedObsolete
                || r.Outcome == ChainOutcome.ParentCompleted;
            bool isSkipped = r.Outcome == ChainOutcome.Skipped;

            if (isSkipped)
            {
                skipped++;
                var skipSuffix = r.SkipReason is not null ? $" - {r.SkipReason}" : "";
                Console.WriteLine($"[{r.TicketId}] Skipped ({durationStr}){skipSuffix}");
            }
            else if (isSuccess)
            {
                completed++;
                Console.WriteLine($"[{r.TicketId}] Completed ({durationStr})");
            }
            else
            {
                failed++;
                var failSuffix = r.FinalRationale is not null ? $" - {r.FinalRationale}" : "";
                Console.WriteLine($"[{r.TicketId}] Failed ({durationStr}){failSuffix}");
            }
        }

        int total = results.Count;
        Console.WriteLine($"{total} ticket{(total == 1 ? "" : "s")}: {completed} completed, {failed} failed, {skipped} skipped");
    }

    private static string FormatChildSummaryLine(ChainResult child)
    {
        var durationStr = FormatDuration(child.TotalDuration);
        return $"  [{child.TicketId}] {child.Outcome} ({durationStr})";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (long)duration.TotalSeconds;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }

    private static int CountImplementRounds(IReadOnlyList<ChainStep> steps)
    {
        // Return the count of implement steps (initial + rework rounds).
        return steps.Count(s => s.PhaseName == "implement");
    }

    private static string GetOperatorTriageSuggestions(
        string ticketId,
        ChainResult result,
        Ticket initialTicket)
    {
        var suggestions = result.Outcome switch
        {
            ChainOutcome.RefusedInitialState =>
                GetRefusedInitialStateTriage(ticketId, initialTicket.State),

            ChainOutcome.ParentStoppedEarly =>
                GetParentStoppedEarlyTriage(ticketId, result),

            ChainOutcome.RefusedDirtyTree =>
                GetRefusedDirtyTreeTriage(ticketId, result),

            ChainOutcome.StoppedAtPlan =>
                GetStoppedAtPlanTriage(ticketId),

            ChainOutcome.StoppedAtImplement =>
                GetStoppedAtImplementTriage(ticketId, result),

            ChainOutcome.StoppedAtReview =>
                GetStoppedAtReviewTriage(ticketId, result),

            ChainOutcome.ReworkCapExceeded =>
                GetReworkCapExceededTriage(ticketId, result),

            ChainOutcome.StoppedAtShip =>
                GetStoppedAtShipTriage(ticketId, result),

            ChainOutcome.ParentHasGrandchildren =>
                GetParentHasGrandchildrenTriage(result),

            _ => null
        };

        if (suggestions == null)
            return string.Empty;

        // Include final rationale if available (for review-based stops). Skip for
        // RefusedDirtyTree and ParentStoppedEarly: no reviewer ran, and the triage text
        // restates the detail itself (the child list, for parent stops).
        var output = new StringBuilder();
        if (!string.IsNullOrEmpty(result.FinalRationale)
            && result.Outcome != ChainOutcome.RefusedDirtyTree
            && result.Outcome != ChainOutcome.ParentStoppedEarly)
        {
            output.AppendLine("Final reviewer rationale:");
            output.AppendLine();
            output.AppendLine(result.FinalRationale);
            output.AppendLine();
        }

        output.Append(suggestions);
        return output.ToString().TrimEnd();
    }

    private static string GetRefusedInitialStateTriage(string ticketId, TicketState state) => state switch
    {
        TicketState.Done =>
            $"Operator triage: {ticketId} is already Done - the chain has nothing to do. If it needs further work, reopen it first ('build reopen {ticketId}').",
        TicketState.Cancelled =>
            $"Operator triage: {ticketId} is Cancelled - the chain will not run it. Reopen it ('build reopen {ticketId}') to bring it back to an active state first.",
        _ =>
            $"Operator triage: chain cannot run from {ticketId}'s current state ({state}). The chain handles Backlog, Ready, InReview, Planning, and InProgress; only Done/Cancelled are refused. Check the ticket state on Plane and transition if needed.",
    };

    private static string GetParentStoppedEarlyTriage(string ticketId, ChainResult result)
    {
        var output = new StringBuilder();
        output.AppendLine($"Operator triage: parent chain {ticketId} stopped because one or more children did not complete.");
        if (result.ChildResults is { Count: > 0 })
        {
            var failed = result.ChildResults
                .Where(c => c.Outcome != ChainOutcome.Completed
                    && c.Outcome != ChainOutcome.RatifiedObsolete
                    && c.Outcome != ChainOutcome.ParentCompleted)
                .ToList();
            foreach (var c in failed)
            {
                var detail = string.IsNullOrEmpty(c.FinalRationale) ? "" : $" - {c.FinalRationale}";
                output.AppendLine($"- {c.TicketId}: {c.Outcome}{detail}");
            }
        }
        output.AppendLine("Completed children are already shipped and are skipped on a re-run.");
        output.Append($"Resume a stopped child directly ('build chain <child-id>'), or fix it ('build review'/'build ship <child-id>'), then re-run: build chain {ticketId}");
        return output.ToString();
    }

    private static string GetRefusedDirtyTreeTriage(string ticketId, ChainResult result)
    {
        var detail = string.IsNullOrEmpty(result.FinalRationale)
            ? "unrelated stash or conflicted paths"
            : result.FinalRationale;

        var output = new StringBuilder();
        output.AppendLine($"Operator triage: chain refused before planning - working tree is not clean: {detail}.");
        output.AppendLine("The git stash stack is repo-global and shared across all worktrees, so a leftover");
        output.AppendLine("stash from unrelated work can be restored into a ticket's tree mid-chain and corrupt it.");
        output.AppendLine("Clean up, then re-run the chain:");
        output.AppendLine("  git stash list                 # see what is stashed");
        output.AppendLine("  git stash show -p stash@{0}    # inspect the contents");
        output.AppendLine("  git stash drop stash@{0}       # discard it (if not needed)");
        output.AppendLine("  # or: git stash pop            # restore it into the working tree, then commit/clean");
        output.AppendLine("If the report named conflicted paths instead, resolve them (git status) and commit or reset.");
        output.Append($"Then re-run: build chain {ticketId}");
        return output.ToString();
    }

    private static string GetStoppedAtPlanTriage(string ticketId)
    {
        return $"Operator triage: Planning failed. Options:\n- Review the planning output above and identify the failure reason.\n- Replan via 'build plan {ticketId}' with adjusted ticket context.\n- Consider closing the ticket if the work is not viable.";
    }

    private static string GetStoppedAtImplementTriage(string ticketId, ChainResult result)
    {
        var layout = GetWorktreeLayoutBestEffort(ticketId);
        var worktreePath = layout?.WorktreePath ?? $".worktrees/ticket-{ticketId}";

        return $"Operator triage: Implementation failed before reaching review. Worktree may need cleanup. Options:\n- Inspect the worktree at {worktreePath} and resolve manually, then 'build ship {ticketId}'.\n- Transition ticket to Cancelled if abandoning.\n- Replan via 'build plan {ticketId}' followed by a new ticket with refined acceptance criteria.";
    }

    private static string GetStoppedAtReviewTriage(string ticketId, ChainResult result)
    {
        return $"Operator triage: Review returned Fail (permanent rejection). Ticket left in InReview state. Options:\n- Inspect the reviewer's feedback above.\n- Transition ticket to Cancelled if work is no longer viable, or back to Backlog for replanning.\n- Consider starting fresh with a new ticket if the scope has fundamentally changed.";
    }

    private static string GetReworkCapExceededTriage(string ticketId, ChainResult result)
    {
        var layout = GetWorktreeLayoutBestEffort(ticketId);
        var worktreePath = layout?.WorktreePath ?? $".worktrees/ticket-{ticketId}";

        var output = new StringBuilder();
        output.AppendLine($"Checks failed:");
        if (result.FinalRationale != null && result.FinalRationale.Contains("Checks failed:"))
        {
            // Extract checks-failed block if present in rationale.
            var lines = result.FinalRationale.Split('\n');
            var inChecksBlock = false;
            foreach (var line in lines)
            {
                if (line.Contains("Checks failed:"))
                    inChecksBlock = true;
                else if (inChecksBlock && !string.IsNullOrEmpty(line) && !line.StartsWith("-"))
                    break;
                else if (inChecksBlock)
                    output.AppendLine(line);
            }
        }
        else
        {
            output.AppendLine("-");
        }

        output.AppendLine();
        output.AppendLine($"Operator triage: ticket left in InReview state. Options:");
        output.AppendLine($"- Inspect the worktree at {worktreePath} and resolve manually, then 'build ship {ticketId}'.");
        output.AppendLine($"- Transition ticket to Cancelled if abandoning.");
        output.AppendLine($"- Replan via 'build close {ticketId} <reason>' followed by a new ticket with refined acceptance criteria.");

        return output.ToString().TrimEnd();
    }

    private static string GetParentHasGrandchildrenTriage(ChainResult result)
    {
        return "Operator triage: This ticket is an operation/parent whose children are themselves parents. " +
               "Chain handles exactly one parent level at a time. " +
               "Chain the intermediate ticket(s) named above directly (e.g. 'build chain <plan-id>'), " +
               "which will fan their leaf briefs out in parallel.";
    }

    private static string GetStoppedAtShipTriage(string ticketId, ChainResult result)
    {
        return $"Operator triage: Ship gate failed; ticket remains in InReview state. Options:\n- Review the gate failure (rebase conflict, regression checks, state consistency).\n- Resolve the failure manually if possible.\n- Retry ship via 'build ship {ticketId}' after resolving.\n- Transition ticket to Cancelled if unable to resolve.";
    }

    private static PhaseWorktreeNames? GetWorktreeLayoutBestEffort(string ticketId)
    {
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            // Placeholder title - we don't have the ticket title here. Use just the ID.
            var layout = PhaseWorktreeLayout.Compute(ticketId, "", cwd);
            return layout;
        }
        catch
        {
            return null;
        }
    }
}
