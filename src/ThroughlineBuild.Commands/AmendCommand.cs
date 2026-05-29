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
        var hasDescription = ctx.Args.TryGetValue("description", out var descriptionArg);
        var hasAc = ctx.Args.TryGetValue("ac", out var acArg);

        if (!hasSize && !hasNote && !hasDescription && !hasAc)
            return new CommandResult(false, "at least one of --size, --note, --description, or --ac is required");

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

        if (hasDescription)
        {
            string descriptionHtml;
            try
            {
                if (descriptionArg == "-")
                {
                    using var reader = new System.IO.StreamReader(Console.OpenStandardInput());
                    descriptionHtml = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
                else
                {
                    descriptionHtml = await System.IO.File.ReadAllTextAsync(descriptionArg!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return new CommandResult(false, $"failed to read description file: {ex.Message}");
            }

            await _ticketing.UpdateDescriptionAsync(ctx.TicketId, descriptionHtml, ct).ConfigureAwait(false);

            await _events.EmitAsync(new WorkflowEvent(
                string.Empty,
                DateTimeOffset.UtcNow,
                EventKind.TicketWrite,
                ctx.TicketId,
                Phase.Command,
                new Dictionary<string, object>
                {
                    ["action"] = "update_description",
                    ["detail"] = "description"
                }), ct).ConfigureAwait(false);
        }

        if (hasAc)
        {
            string acHtml;
            try
            {
                if (acArg == "-")
                {
                    using var reader = new System.IO.StreamReader(Console.OpenStandardInput());
                    acHtml = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
                else
                {
                    acHtml = await System.IO.File.ReadAllTextAsync(acArg!, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return new CommandResult(false, $"failed to read ac file: {ex.Message}");
            }

            await _ticketing.UpdateDescriptionAsync(ctx.TicketId, acHtml, ct).ConfigureAwait(false);

            await _events.EmitAsync(new WorkflowEvent(
                string.Empty,
                DateTimeOffset.UtcNow,
                EventKind.TicketWrite,
                ctx.TicketId,
                Phase.Command,
                new Dictionary<string, object>
                {
                    ["action"] = "update_description",
                    ["detail"] = "ac"
                }), ct).ConfigureAwait(false);
        }

        return new CommandResult(true, null);
    }
}
