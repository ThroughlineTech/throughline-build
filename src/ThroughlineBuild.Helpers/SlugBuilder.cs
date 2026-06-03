using System.Text.RegularExpressions;

namespace ThroughlineBuild.Helpers;

public static class SlugBuilder
{
    private static readonly Regex NonAlphanumericPattern = new(@"[^a-z0-9-]", RegexOptions.Compiled);
    private static readonly Regex ConsecutiveHyphensPattern = new(@"-+", RegexOptions.Compiled);

    /// <summary>
    /// Builds the slug used for branch and worktree names. This is just the sanitized,
    /// lowercased ticket id (no title) - keeping worktree directory paths short so deep
    /// repo trees stay under the Windows MAX_PATH limit. Equivalent to BuildBranchSlug
    /// with an empty title.
    /// </summary>
    public static string BuildTicketSlug(string ticketId) => BuildBranchSlug(ticketId, "");

    public static string BuildBranchSlug(string ticketId, string title)
    {
        // Step 1: Lowercase both inputs
        var lowerTicketId = ticketId.ToLowerInvariant();
        var lowerTitle = title.ToLowerInvariant();

        // Step 2: Strip non-ASCII characters from the title
        var asciiTitle = StripNonAscii(lowerTitle);

        // Step 3: Replace non-alphanumeric runs with single hyphen
        var processedTitle = NonAlphanumericPattern.Replace(asciiTitle, "-");

        // Step 4: Trim leading and trailing hyphens from processed title
        processedTitle = processedTitle.Trim('-');

        // Step 5: Join ticket ID and processed title
        var combined = string.IsNullOrEmpty(processedTitle)
            ? lowerTicketId
            : $"{lowerTicketId}-{processedTitle}";

        // Step 6: Truncate to 80 characters on hyphen boundary if possible
        if (combined.Length > 80)
        {
            combined = TruncateToEightyChars(combined);
        }

        // Step 7: Strip leading/trailing hyphens from final result
        combined = combined.Trim('-');

        // Step 8: Collapse consecutive hyphens to single hyphen
        combined = ConsecutiveHyphensPattern.Replace(combined, "-");

        return combined;
    }

    private static string StripNonAscii(string input)
    {
        var result = new System.Text.StringBuilder();
        foreach (var c in input)
        {
            if (c <= 127)
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string TruncateToEightyChars(string input)
    {
        if (input.Length <= 80)
            return input;

        // Truncate to 80 characters
        var truncated = input.Substring(0, 80);

        // Walk back to find a hyphen boundary to avoid splitting a word
        var lastHyphen = truncated.LastIndexOf('-');
        if (lastHyphen > 0)
        {
            return truncated.Substring(0, lastHyphen);
        }

        // If no hyphen found, return the truncated string as-is
        return truncated;
    }
}
