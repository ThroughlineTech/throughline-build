namespace ThroughlineBuild.Contracts;

public record CommandResult(bool Success, string? Message);

public record TicketCommandContext(string TicketId, IReadOnlyDictionary<string, string> Args);

public interface ITicketCommand
{
    Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct);
}
