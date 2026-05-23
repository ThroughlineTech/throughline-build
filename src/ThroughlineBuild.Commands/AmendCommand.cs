using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Commands;

public sealed class AmendCommand : ITicketCommand
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;

    public AmendCommand(ITicketing ticketing, IEventSink events)
    {
        _ticketing = ticketing;
        _events = events;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        var hasSize = ctx.Args.TryGetValue("size", out var sizeArg);
        var hasNote = ctx.Args.TryGetValue("note", out var noteArg);

        if (!hasSize && !hasNote)
            return new CommandResult(false, "at least one of --size or --note is required");

        if (hasSize)
        {
            var sizeUpper = sizeArg!.ToUpperInvariant();
            if (sizeUpper != "S" && sizeUpper != "M" && sizeUpper != "L")
                return new CommandResult(false, "invalid --size value; expected S, M, or L");
        }

        var ticket = await _ticketing.GetAsync(ctx.TicketId, ct).ConfigureAwait(false);

        if (ticket.State == TicketState.Done || ticket.State == TicketState.Cancelled)
            return new CommandResult(false, "Cancelled or Done; reopen first");

        if (hasSize)
        {
            var newLabels = ticket.Labels
                .Where(l => !l.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { $"size:{sizeArg!.ToLowerInvariant()}" })
                .ToList();

            await _ticketing.ApplyLabelsAsync(ctx.TicketId, newLabels, ct).ConfigureAwait(false);

            await _events.EmitAsync(new WorkflowEvent(
                string.Empty,
                DateTimeOffset.UtcNow,
                EventKind.TicketWrite,
                ctx.TicketId,
                Phase.Command,
                new Dictionary<string, object>
                {
                    ["action"] = "apply_labels",
                    ["detail"] = "size"
                }), ct).ConfigureAwait(false);
        }

        if (hasNote)
        {
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            var contextNoteHtml = $"<hr/><h3>Context Note</h3><p><em>Added {today}</em></p><p>{noteArg}</p>";

            await _ticketing.AppendDescriptionAsync(ctx.TicketId, contextNoteHtml, ct).ConfigureAwait(false);

            await _events.EmitAsync(new WorkflowEvent(
                string.Empty,
                DateTimeOffset.UtcNow,
                EventKind.TicketWrite,
                ctx.TicketId,
                Phase.Command,
                new Dictionary<string, object>
                {
                    ["action"] = "append_description",
                    ["detail"] = "note"
                }), ct).ConfigureAwait(false);
        }

        return new CommandResult(true, null);
    }
}
