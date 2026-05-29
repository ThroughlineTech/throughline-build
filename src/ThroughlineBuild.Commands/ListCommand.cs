using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Commands;

/// <summary>
/// ListCommand queries tickets with optional filters and renders tabular output.
/// Supports filtering by --state, --parent, and --type.
/// </summary>
public sealed class ListCommand : ITicketCommand
{
    private readonly ITicketing _ticketing;
    private readonly TextWriter _output;

    public ListCommand(ITicketing ticketing, TextWriter? output = null)
    {
        _ticketing = ticketing;
        _output = output ?? Console.Out;
    }

    public async Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct)
    {
        try
        {
            // Parse filter flags from ctx.Args
            TicketState? stateFilter = null;
            string? parentFilter = null;
            string? typeFilter = null;

            if (ctx.Args.TryGetValue("state", out var stateArg) && !string.IsNullOrWhiteSpace(stateArg))
            {
                if (!Enum.TryParse<TicketState>(stateArg, ignoreCase: true, out var state))
                    return new CommandResult(false, $"invalid --state value: {stateArg}");
                stateFilter = state;
            }

            if (ctx.Args.TryGetValue("parent", out var parentArg) && !string.IsNullOrWhiteSpace(parentArg))
                parentFilter = parentArg;

            if (ctx.Args.TryGetValue("type", out var typeArg) && !string.IsNullOrWhiteSpace(typeArg))
                typeFilter = typeArg;

            // Query tickets with filters
            var query = new TicketQuery(State: stateFilter, ParentId: parentFilter, Type: typeFilter);
            var tickets = await _ticketing.QueryAsync(query, ct).ConfigureAwait(false);

            if (tickets.Count == 0)
            {
                _output.WriteLine("no tickets found");
                return new CommandResult(true, null);
            }

            // Render tabular output
            RenderTable(tickets);

            return new CommandResult(true, null);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, ex.Message);
        }
    }

    private void RenderTable(IReadOnlyList<Ticket> tickets)
    {
        // Compute column widths from data
        var idWidth = Math.Max(2, tickets.Max(t => t.Id.Length));
        var titleWidth = Math.Min(50, Math.Max(5, tickets.Max(t => t.Title.Length)));
        var stateWidth = Math.Max(5, tickets.Max(t => t.State.ToString().Length));
        var typeWidth = Math.Max(4, tickets.Max(t => t.Type.Length));
        var parentTickets = tickets.Where(t => t.ParentId != null).ToList();
        var parentWidth = Math.Max(6, parentTickets.Count > 0 ? parentTickets.Max(t => (t.ParentId?.Length) ?? 0) : 0);

        // Header row
        var headerId = "ID".PadRight(idWidth);
        var headerTitle = "Title".PadRight(titleWidth);
        var headerState = "State".PadRight(stateWidth);
        var headerType = "Type".PadRight(typeWidth);
        var headerParent = "Parent";

        _output.WriteLine($"{headerId} {headerTitle} {headerState} {headerType} {headerParent}");

        // Separator
        _output.WriteLine(new string('-', idWidth + 1 + titleWidth + 1 + stateWidth + 1 + typeWidth + 1 + parentWidth));

        // Data rows
        foreach (var ticket in tickets)
        {
            var truncatedTitle = ticket.Title.Length > 50
                ? ticket.Title.Substring(0, 47) + "..."
                : ticket.Title;

            var id = ticket.Id.PadRight(idWidth);
            var title = truncatedTitle.PadRight(titleWidth);
            var state = ticket.State.ToString().PadRight(stateWidth);
            var type = ticket.Type.PadRight(typeWidth);
            var parent = ticket.ParentId ?? "";

            _output.WriteLine($"{id} {title} {state} {type} {parent}");
        }
    }
}
