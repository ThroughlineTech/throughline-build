namespace ThroughlineBuild.Helpers;

public static class TicketIdOrdering
{
    /// <summary>
    /// Extracts the trailing integer from a prefixed ticket id, or parses a bare numeric id.
    /// Malformed ids sort last when callers use the returned value as their primary key.
    /// </summary>
    public static int Number(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash >= 0
            && dash < id.Length - 1
            && int.TryParse(id.AsSpan(dash + 1), out var number))
        {
            return number;
        }

        if (dash < 0 && int.TryParse(id, out var bare))
            return bare;

        return int.MaxValue;
    }
}
