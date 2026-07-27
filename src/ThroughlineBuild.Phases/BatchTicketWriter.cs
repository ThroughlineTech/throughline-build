using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Runs hard batch ticket writes while preserving the ticket that was active when a backend
/// outage occurred, allowing the chain boundary to degrade the batch into resumable outcomes.
/// </summary>
internal static class BatchTicketWriter
{
    internal static async Task RunBatchStateWriteAsync(string ticketId, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (TicketingUnavailableException ex)
        {
            throw new BatchTicketingUnavailableException(ticketId, ex);
        }
    }
}

internal sealed class BatchTicketingUnavailableException(
    string ticketId,
    TicketingUnavailableException innerException) : Exception(innerException.Message, innerException)
{
    public string TicketId { get; } = ticketId;
    public TicketingUnavailableException TicketingException { get; } = innerException;
}
