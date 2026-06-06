using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Reads the current small/medium/large tiers from a config's [workers.codex.sizes]
/// block, tolerating both active and commented forms. Used by 'build models refresh'
/// to render a current-to-proposed diff. Pure - no IO.
///
/// Returns the tiers it can parse; a tier whose line is missing or malformed is simply
/// absent from the returned dictionary. Returns null when the config has no codex-sizes
/// block at all (the caller treats that as "nothing to refresh").
/// </summary>
public static class CodexSizesBlockReader
{
    // Matches:  small = { model = "x" }   or   medium = { model = "x", effort = "y" }
    // after the caller strips any leading "#" comment markers and indentation.
    private static readonly Regex SizeLine = new(
        "^(small|medium|large)\\s*=\\s*\\{\\s*model\\s*=\\s*\"([^\"]*)\"\\s*(?:,\\s*effort\\s*=\\s*\"([^\"]*)\")?\\s*\\}");

    public static IReadOnlyDictionary<WorkerSize, ModelTier>? Read(string configText)
    {
        if (!CodexSizesBlockEditor.TryFindCodexSizesBlock(configText, out int start, out int endExclusive))
            return null;

        var lines = configText.Split('\n');
        var result = new Dictionary<WorkerSize, ModelTier>();
        for (int i = start; i < endExclusive && i < lines.Length; i++)
        {
            var content = Decomment(lines[i]);
            var match = SizeLine.Match(content);
            if (!match.Success)
                continue;

            var size = match.Groups[1].Value switch
            {
                "small" => WorkerSize.Small,
                "medium" => WorkerSize.Medium,
                _ => WorkerSize.Large,
            };
            var model = match.Groups[2].Value;
            var effort = match.Groups[3].Success && match.Groups[3].Value.Length > 0
                ? match.Groups[3].Value
                : null;
            result[size] = new ModelTier(model, effort);
        }
        return result;
    }

    // Strip leading whitespace and any run of "#" comment markers (plus following spaces),
    // tolerating a trailing '\r' on CRLF inputs.
    private static string Decomment(string line)
    {
        var trimmed = line.TrimEnd('\r').TrimStart();
        while (trimmed.StartsWith("#"))
            trimmed = trimmed.Substring(1).TrimStart();
        return trimmed;
    }
}
