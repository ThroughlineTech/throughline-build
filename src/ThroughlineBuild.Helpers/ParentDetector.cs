using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Helpers;

public static class ParentDetector
{
    public static async Task<bool> HasChildrenAsync(ITicketing ticketing, string ticketUuid, CancellationToken ct)
    {
        var children = await ticketing.QueryAsync(new TicketQuery(ParentId: ticketUuid), ct);
        return children.Count > 0;
    }
}
