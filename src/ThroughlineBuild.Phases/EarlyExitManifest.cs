using System.Globalization;
using System.Text;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Helper to write a manifest file when ImplementPhase exits early (before the worker runs).
/// Writes a JSON object with phase, ticket_id, and reason fields to phase-status.json.
/// No-op when captureDir is null.
/// </summary>
internal static class EarlyExitManifest
{
    /// <summary>
    /// Write a phase-status.json manifest to the capture directory describing an early exit.
    /// No-op if captureDir is null. Exceptions are swallowed (best-effort).
    /// </summary>
    public static void Write(string? captureDir, string phase, string ticketId, string reason)
    {
        if (captureDir is null)
            return;

        try
        {
            Directory.CreateDirectory(captureDir);

            // Manual JSON string builder (AOT-safe, avoids JsonSerializer source-gen context)
            var json = BuildJson(phase, ticketId, reason);
            var filePath = Path.Combine(captureDir, "phase-status.json");
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }
        catch
        {
            // Best-effort: failure to write debug artifacts never overrides the phase failure reason.
        }
    }

    private static string BuildJson(string phase, string ticketId, string reason)
    {
        var sb = new StringBuilder();
        sb.Append("{\"phase\":\"");
        sb.Append(EscapeJsonString(phase));
        sb.Append("\",\"ticket_id\":\"");
        sb.Append(EscapeJsonString(ticketId));
        sb.Append("\",\"reason\":\"");
        sb.Append(EscapeJsonString(reason));
        sb.Append("\"}");
        return sb.ToString();
    }

    private static string EscapeJsonString(string value)
    {
        if (value is null)
            return "";

        var sb = new StringBuilder();
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    // Control characters below 0x20 must be escaped
                    if (c < 0x20)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
