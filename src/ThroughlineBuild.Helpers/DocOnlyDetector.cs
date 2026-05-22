namespace ThroughlineBuild.Helpers;

public static class DocOnlyDetector
{
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md",
        ".markdown",
        ".txt",
        ".rst",
        ".adoc"
    };

    /// <summary>
    /// Determines if all files in the provided list are documentation-only files.
    /// </summary>
    /// <param name="changedFiles">The list of file paths to check.</param>
    /// <returns>
    /// True if all files qualify as documentation; false if any file is not documentation or the list is empty.
    /// </returns>
    public static bool IsDocOnly(IEnumerable<string> changedFiles)
    {
        var files = changedFiles.ToList();

        // Empty list returns false
        if (files.Count == 0)
        {
            return false;
        }

        // Check each file to see if it's a doc file
        foreach (var file in files)
        {
            if (!IsDocFile(file))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDocFile(string filePath)
    {
        // Normalize path separators to forward slashes for consistency
        var normalizedPath = filePath.Replace('\\', '/');

        // Check if any directory component is "docs" or "documentation" (case-insensitive)
        var pathComponents = normalizedPath.Split('/');
        foreach (var component in pathComponents)
        {
            if (component.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("documentation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Extract the filename (last component)
        var filename = Path.GetFileName(normalizedPath);

        // Check if filename starts with "readme" (case-insensitive, any extension or none)
        if (filename.StartsWith("readme", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if file has a doc extension
        var extension = Path.GetExtension(filePath);
        if (DocExtensions.Contains(extension))
        {
            return true;
        }

        return false;
    }
}
