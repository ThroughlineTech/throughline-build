using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.JudgmentSlots;

namespace ThroughlineBuild.Commands;

/// <summary>
/// ReopenCommand transitions a terminal ticket (Done or Cancelled) back to an
/// active state (Backlog or Ready). The destination is inferred from the prior
/// terminal marker (wontfix: or deferred:) and the description content.
///
/// Audit-trail invariant: this command MUST NOT modify the ticket description
/// or labels. It only posts a new "reopened:" comment and transitions state.
/// </summary>
public sealed class ReopenCommand : ITicketCommand
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly ReasonTranslator _translator;

    public ReopenCommand(
        ITicketing ticketing,
        IEventSink events,
        ReasonTranslator translator)
    {
        _ticketing = ticketing;
        _events = events;
        _translator = translator;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        // (a) Inspect ticket state - reject non-terminal states
        var ticket = await _ticketing.GetAsync(ctx.TicketId, ct).ConfigureAwait(false);
        if (ticket.State != TicketState.Done && ticket.State != TicketState.Cancelled)
            return new CommandResult(false, "already active");

        // (a2) Parent check: note only - children are not reopened automatically
        var reopenChildren = await _ticketing.QueryAsync(new TicketQuery(ParentId: ticket.Uuid), ct).ConfigureAwait(false);
        if (reopenChildren.Count > 0)
        {
            Console.Error.WriteLine("Note: this is a parent ticket. Children are not reopened automatically.");
        }

        // (b) Determine reason text. If supplied, translate (in case non-English);
        //     otherwise synthesize an English default - no translation needed.
        string reason;
        if (ctx.Args.TryGetValue("reason", out var rawReason) && !string.IsNullOrWhiteSpace(rawReason))
        {
            reason = await _translator.TranslateAsync(rawReason, ct).ConfigureAwait(false);
        }
        else
        {
            reason = $"reopened on {DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}";
        }

        // (c) Scan prior comments most-recent-first for a terminal marker.
        //     The marker strings deferred: and wontfix: are load-bearing - they
        //     are produced by DeferCommand and CloseCommand respectively.
        var comments = await _ticketing.GetCommentsAsync(ctx.TicketId, ct).ConfigureAwait(false);
        string? priorMarker = null;
        foreach (var c in comments.OrderByDescending(c => c.CreatedAt))
        {
            var commentBody = c.Body ?? string.Empty;
            if (commentBody.Contains("<strong>deferred:</strong>", StringComparison.OrdinalIgnoreCase))
            {
                priorMarker = "deferred:";
                break;
            }
            if (commentBody.Contains("<strong>wontfix:</strong>", StringComparison.OrdinalIgnoreCase))
            {
                priorMarker = "wontfix:";
                break;
            }
        }

        // (d) Determine destination state
        var targetState = DetermineTargetState(ticket, priorMarker);

        // (e) Transition to target state via lifecycle method - handles comment posting internally
        // Format reason with prior marker context for TransitionLifecycleAsync
        var fullReason = $"from {priorMarker ?? "unknown"} - {reason}";
        await _ticketing.TransitionLifecycleAsync(ctx.TicketId, LifecycleTransition.Reopen, fullReason, ct).ConfigureAwait(false);
        await _events.EmitAsync(new WorkflowEvent(
            string.Empty,
            DateTimeOffset.UtcNow,
            EventKind.StateTransition,
            ctx.TicketId,
            Phase.Command,
            new Dictionary<string, object>
            {
                ["from"] = ticket.State.ToString(),
                ["to"] = targetState.ToString()
            }), ct).ConfigureAwait(false);

        return new CommandResult(true, null);
    }

    /// <summary>
    /// Destination state rules:
    /// - Done -> Backlog
    /// - Cancelled with prior "deferred:" marker AND description contains
    ///   "&lt;h3&gt;Implementation Plan&lt;/h3&gt;" -> Ready
    /// - Cancelled with prior "deferred:" marker AND no Implementation Plan -> Backlog
    /// - Cancelled with prior "wontfix:" marker -> Backlog
    /// - Default (no parseable marker / any doubt) -> Backlog
    /// </summary>
    private static TicketState DetermineTargetState(Ticket ticket, string? priorMarker)
    {
        if (ticket.State == TicketState.Done)
            return TicketState.Backlog;

        if (ticket.State == TicketState.Cancelled)
        {
            if (priorMarker is not null && priorMarker.StartsWith("deferred:", StringComparison.Ordinal))
            {
                var desc = ticket.DescriptionHtml ?? string.Empty;
                if (desc.Contains("<h3>Implementation Plan</h3>", StringComparison.OrdinalIgnoreCase))
                    return TicketState.Ready;
                return TicketState.Backlog;
            }

            if (priorMarker is not null && priorMarker.StartsWith("wontfix:", StringComparison.Ordinal))
                return TicketState.Backlog;
        }

        return TicketState.Backlog;
    }
}
