using System.Text.RegularExpressions;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Detects a literal (non-placeholder) plane_api_token value in a config.toml's raw text. Now that
/// config.toml is tracked (see TLB-627), this is the safety net for an operator who used --token or
/// hand-edited an older repo's config: a tracked file with a live Plane token is a credential leak
/// waiting to happen. Pure text scan - no TOML parsing, no I/O, and the token value itself is never
/// echoed back in the remediation message. Shared by 'sop doctor' (raw file read, never loads
/// ticketing config) and 'setup --check' (already has the full config loaded, but reuses this scan
/// for one consistent finding message in both places).
/// </summary>
public static class InlineTokenScanner
{
    public const string FindingCode = "ticketing.plane_api_token.inline_secret";

    // Anchored at the start of a line (so a commented-out "# plane_api_token = ..." alternative is
    // never matched) and requires "=" immediately after the key name, so plane_api_token_file is
    // never matched either.
    private static readonly Regex Pattern = new(
        @"^plane_api_token\s*=\s*""([^""]*)""",
        RegexOptions.Multiline);

    /// <summary>
    /// Returns a remediation message when <paramref name="configText"/> sets plane_api_token to a
    /// non-empty, non-placeholder literal value; otherwise null.
    /// </summary>
    public static string? Scan(string configText)
    {
        var match = Pattern.Match(configText);
        if (!match.Success)
            return null;

        var value = match.Groups[1].Value;
        if (value.Length == 0 || value.StartsWith("REQUIRED_", StringComparison.Ordinal))
            return null;

        return "config.toml sets plane_api_token to a literal value, which must not be committed. "
            + "Replace the line with plane_api_token_env = \"PLANE_API_TOKEN\" and run: "
            + "export PLANE_API_TOKEN=<your token>";
    }
}
