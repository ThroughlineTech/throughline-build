using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Applies the orchestration policy for non-state-bearing ticketing writes. Informational
/// comments and label updates are best-effort: after the backend client has exhausted its own
/// retries, the failure is recorded locally and workflow execution continues. State transitions
/// and resume-oracle markers must never use this helper; their failures stay hard so the chain
/// returns a resumable environmental outcome.
/// </summary>
internal static class TicketingWritePolicy
{
    public static async Task<bool> BestEffortAsync(
        IEventSink events,
        string sessionId,
        string ticketId,
        Phase phase,
        string operation,
        Func<Task> write,
        TextWriter diagnostics,
        CancellationToken ct)
    {
        try
        {
            await write().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.WriteLine(
                $"[{ticketId}] warning: ticketing write '{operation}' failed ({ex.Message}); continuing");
            try
            {
                await events.EmitAsync(new WorkflowEvent(
                    SessionId: sessionId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.TicketWrite,
                    TicketId: ticketId,
                    Phase: phase,
                    Data: new Dictionary<string, object>
                    {
                        ["action"] = "ticketing_write_failed",
                        ["operation"] = operation,
                        ["error"] = ex.Message
                    }), ct).ConfigureAwait(false);
            }
            catch
            {
                // The event log is forensic and must not make an informational write fatal.
            }
            return false;
        }
    }
}
