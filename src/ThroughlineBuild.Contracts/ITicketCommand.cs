namespace ThroughlineBuild.Contracts;

public record CommandResult(bool Success, string? Message);

public record TicketCommandContext(
    string TicketId,
    IReadOnlyDictionary<string, string> Args,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RepeatedArgs = null)
{
    /// <summary>
    /// Returns every value supplied for a repeatable option. Contexts created by older callers
    /// still expose their single dictionary value as a one-item list.
    /// </summary>
    public IReadOnlyList<string> GetValues(string key)
    {
        if (RepeatedArgs is not null && RepeatedArgs.TryGetValue(key, out var values))
            return values;

        return Args.TryGetValue(key, out var value) ? [value] : [];
    }
}

public interface ITicketCommand
{
    Task<CommandResult> ExecuteAsync(TicketCommandContext ctx, CancellationToken ct);
}
