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
        var hasTitle = ctx.Args.TryGetValue("title", out var titleArg);
        var hasPriority = ctx.Args.TryGetValue("priority", out var priorityArg);
        var hasType = ctx.Args.TryGetValue("type", out var typeArg);
        var hasParent = ctx.Args.TryGetValue("parent", out var parentArg);
        var labelsToAdd = ctx.GetValues("label-add");
        var labelsToRemove = ctx.GetValues("label-remove");
        var hasLabelChanges = labelsToAdd.Count > 0 || labelsToRemove.Count > 0;

        if (!hasSize && !hasNote && !hasDescription && !hasAc && !hasTitle && !hasPriority
            && !hasType && !hasParent && !hasLabelChanges)
        {
            return new CommandResult(false,
                "at least one amend option is required (--title, --priority, --type, --label-add, " +
                "--label-remove, --parent, --size, --note, --description, or --ac)");
        }

        if (hasSize)
        {
            var sizeUpper = sizeArg!.ToUpperInvariant();
            if (sizeUpper != "S" && sizeUpper != "M" && sizeUpper != "L")
                return new CommandResult(false, "invalid --size value; expected S, M, or L");
        }

        if (hasTitle && string.IsNullOrWhiteSpace(titleArg))
            return new CommandResult(false, "--title requires a non-empty value");

        string? normalizedPriority = null;
        if (hasPriority)
        {
            normalizedPriority = priorityArg!.ToLowerInvariant();
            if (normalizedPriority is not ("urgent" or "high" or "medium" or "low" or "none"))
                return new CommandResult(false, "invalid --priority value; expected urgent, high, medium, low, or none");
        }

        if (hasType && string.IsNullOrWhiteSpace(typeArg))
            return new CommandResult(false, "--type requires a non-empty value");
        if (hasParent && string.IsNullOrWhiteSpace(parentArg))
            return new CommandResult(false, "--parent requires a non-empty ticket id");
        if (hasParent && string.Equals(ctx.TicketId, parentArg, StringComparison.OrdinalIgnoreCase))
            return new CommandResult(false, "a ticket cannot be its own parent");
        if (labelsToAdd.Concat(labelsToRemove).Any(string.IsNullOrWhiteSpace))
            return new CommandResult(false, "--label-add and --label-remove require non-empty values");

        // Materialize file/stdin inputs before any backend write. A later unreadable input must not
        // leave earlier scalar mutations partially applied.
        string? descriptionHtml = null;
        if (hasDescription)
        {
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
        }

        string? acHtml = null;
        if (hasAc)
        {
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
        }

        var ticket = await _ticketing.GetAsync(ctx.TicketId, ct).ConfigureAwait(false);

        if (ticket.State == TicketState.Done || ticket.State == TicketState.Cancelled)
            return new CommandResult(false, "Cancelled or Done; reopen first");

        // Resolve the parent before any mutation so a bad parent id cannot leave earlier fields
        // partially amended. SetParentAsync still receives only backend UUIDs.
        var parent = hasParent
            ? await _ticketing.GetRelationTicketAsync(parentArg!, ct).ConfigureAwait(false)
            : null;

        List<string>? updatedLabels = null;
        if (hasSize || hasLabelChanges)
        {
            updatedLabels = ticket.Labels
                .Where(l => !hasSize || !l.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                .Where(l => !labelsToRemove.Contains(l, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (hasSize)
                updatedLabels.Add($"size:{sizeArg!.ToLowerInvariant()}");
            foreach (var label in labelsToAdd)
            {
                if (!updatedLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                    updatedLabels.Add(label);
            }
        }

        // Resolve every backend name before the first mutation. Validation uses the same cached
        // Plane maps as the write path, so successful preflight adds no duplicate network lookup.
        try
        {
            if (hasType)
                await _ticketing.ValidateTypeAsync(typeArg!, ct).ConfigureAwait(false);
            if (updatedLabels is not null)
                await _ticketing.ValidateLabelsAsync(updatedLabels, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return new CommandResult(false, ex.Message);
        }

        if (hasTitle)
        {
            await _ticketing.UpdateTitleAsync(ctx.TicketId, titleArg!, ct).ConfigureAwait(false);
            await EmitWriteAsync(ctx.TicketId, "update_title", "title", ct).ConfigureAwait(false);
        }

        if (hasPriority)
        {
            await _ticketing.UpdatePriorityAsync(ctx.TicketId, normalizedPriority!, ct).ConfigureAwait(false);
            await EmitWriteAsync(ctx.TicketId, "update_priority", "priority", ct).ConfigureAwait(false);
        }

        if (hasType)
        {
            await _ticketing.UpdateTypeAsync(ctx.TicketId, typeArg!, ct).ConfigureAwait(false);
            await EmitWriteAsync(ctx.TicketId, "update_type", "type", ct).ConfigureAwait(false);
        }

        if (hasSize || hasLabelChanges)
        {
            await _ticketing.ApplyLabelsAsync(ctx.TicketId, updatedLabels!, ct).ConfigureAwait(false);
            await EmitWriteAsync(ctx.TicketId, "apply_labels", hasSize ? "size/labels" : "labels", ct)
                .ConfigureAwait(false);
        }

        if (hasParent)
        {
            await _ticketing.SetParentAsync(ticket.Uuid, parent!.Uuid, ct).ConfigureAwait(false);
            await EmitWriteAsync(ctx.TicketId, "set_parent", parentArg!, ct).ConfigureAwait(false);
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
            await _ticketing.UpdateDescriptionAsync(ctx.TicketId, descriptionHtml!, ct).ConfigureAwait(false);

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
            await _ticketing.UpdateDescriptionAsync(ctx.TicketId, acHtml!, ct).ConfigureAwait(false);

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

    private Task EmitWriteAsync(string ticketId, string action, string detail, CancellationToken ct) =>
        _events.EmitAsync(new WorkflowEvent(
            string.Empty,
            DateTimeOffset.UtcNow,
            EventKind.TicketWrite,
            ticketId,
            Phase.Command,
            new Dictionary<string, object>
            {
                ["action"] = action,
                ["detail"] = detail
            }), ct);
}
