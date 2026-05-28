using System.Text;

namespace ThroughlineBuild.EventLog;

// Produces a human-scannable filename stem for the per-invocation JSONL log
// (and the parallel `.build/sessions/` debug-capture directory when --debug
// is set). The same stem is used for both so the two on-disk artifacts of a
// single CLI run line up under a shared prefix when sorted by name.
//
// Shape: "{project}-{ticket-or-slug}-{verb}-{yyyy-MM-dd}-{HHmmss}"
// Example: "latticeflow-TLB-169-implement-2026-05-28-143052"
//
// - Project is lowercased; ticket id keeps its canonical case (TLB-169) so it
//   matches Plane and the user's mental model.
// - Time suffix is required for collision-safety: a second run of the same
//   verb on the same ticket on the same day must not Append onto the first
//   run's events file (the sink opens in FileMode.Append).
public static class SessionFileNameBuilder
{
    public static string Build(
        string? projectName,
        string? projectIdentifier,
        string verb,
        string? ticketId,
        string? extraSlug,
        DateTimeOffset timestamp)
    {
        var parts = new List<string>(5);

        var project = SlugifyLower(projectName);
        if (string.IsNullOrEmpty(project))
            project = SlugifyLower(projectIdentifier);
        if (!string.IsNullOrEmpty(project))
            parts.Add(project);

        if (!string.IsNullOrEmpty(ticketId))
            parts.Add(SanitizePreserveCase(ticketId));
        else if (!string.IsNullOrEmpty(extraSlug))
            parts.Add(SlugifyLower(extraSlug));

        if (string.IsNullOrEmpty(verb))
            parts.Add("run");
        else
            parts.Add(SlugifyLower(verb));

        parts.Add(timestamp.ToString("yyyy-MM-dd-HHmmss"));

        return string.Join("-", parts);
    }

    // Lowercase, non-alphanumeric -> hyphen, collapsed and trimmed.
    public static string SlugifyLower(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(s!.Length);
        bool lastWasDash = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (sb.Length > 0 && !lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        while (sb.Length > 0 && sb[sb.Length - 1] == '-')
            sb.Length--;
        return sb.ToString();
    }

    // Preserve case (for ticket ids like "TLB-169"); strip path-unfriendly chars.
    private static string SanitizePreserveCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWasDash = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
                lastWasDash = c == '-';
            }
            else if (sb.Length > 0 && !lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        while (sb.Length > 0 && sb[sb.Length - 1] == '-')
            sb.Length--;
        return sb.ToString();
    }
}
