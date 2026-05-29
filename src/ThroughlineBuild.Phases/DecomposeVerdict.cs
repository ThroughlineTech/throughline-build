namespace ThroughlineBuild.Phases;

internal static class DecomposeVerdict
{
    public static IReadOnlyList<string> Check(IReadOnlyList<ChildSpec> specs)
    {
        var failures = new List<string>();

        // coverage_check: every child must have a non-empty scope_boundary
        var noScope = specs.Where(s => string.IsNullOrWhiteSpace(s.ScopeBoundary)).ToList();
        if (noScope.Any())
            failures.Add($"coverage_check: {noScope.Count} child(ren) missing scope_boundary: {string.Join(", ", noScope.Select(s => s.Title))}");

        // uniqueness_check: no two children share an identical title (case-insensitive)
        var dupes = specs.GroupBy(s => s.Title?.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Key)
                         .ToList();
        if (dupes.Any())
            failures.Add($"uniqueness_check: duplicate titles: {string.Join(", ", dupes)}");

        // size_check: every child must have a recognized size (S, M, or L)
        var validSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S", "M", "L" };
        var badSize = specs.Where(s => !validSizes.Contains(s.Size ?? "")).ToList();
        if (badSize.Any())
            failures.Add($"size_check: {badSize.Count} child(ren) have unrecognized size: {string.Join(", ", badSize.Select(s => $"{s.Title}={s.Size}"))}");

        return failures;
    }
}
