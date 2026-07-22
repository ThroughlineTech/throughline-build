namespace ThroughlineBuild.Contracts;

/// <summary>Canonical Plane work-item relation names and tolerant CLI normalization.</summary>
public static class RelationKinds
{
    public static readonly IReadOnlyList<string> Allowed =
    [
        "relates_to", "duplicate", "blocked_by", "blocking", "start_before",
        "start_after", "finish_before", "finish_after", "implemented_by", "implements"
    ];

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim().ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_');
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        return Allowed.Contains(normalized, StringComparer.Ordinal);
    }
}
