namespace ThroughlineBuild.Helpers;

public static class DriftComparator
{
    public static DriftResult Compare(
        string markerSha,
        string currentSha,
        IReadOnlyList<string> relevantFiles,
        IReadOnlyList<string> filesChangedBetween)
    {
        // If SHAs match, no drift regardless of files
        if (markerSha == currentSha)
        {
            return new DriftResult(false, new List<string>());
        }

        // Compute intersection of relevantFiles and filesChangedBetween (case-sensitive)
        var overlappingFiles = relevantFiles
            .Where(f => filesChangedBetween.Contains(f))
            .OrderBy(f => f)
            .ToList();

        var hasDrift = overlappingFiles.Count > 0;
        return new DriftResult(hasDrift, overlappingFiles);
    }
}

public record DriftResult(bool HasDrift, IReadOnlyList<string> OverlappingFiles);
