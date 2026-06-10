namespace ThroughlineBuild.Contracts;

/// <summary>
/// Thrown by a ticketing backend when the service is unreachable at the transport level
/// (DNS resolution, connect, TLS handshake, timeout) after the client's transport retries
/// are exhausted. This is environmental, not ticket-attributable: orchestration layers
/// catch it to stop the current ticket with a resumable outcome instead of crashing the
/// process, and skip remaining work that would hit the same wall - mirroring the TLB-538
/// environment-failure classification (TLB-545).
/// </summary>
public sealed class TicketingUnavailableException : Exception
{
    public TicketingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
