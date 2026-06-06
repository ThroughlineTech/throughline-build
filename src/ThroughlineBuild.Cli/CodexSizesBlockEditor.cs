namespace ThroughlineBuild.Cli;

/// <summary>
/// In-place replacement of the [workers.codex.sizes] block in a config text. Pure - no IO.
///
/// The header line may be active ("[workers.codex.sizes]") OR commented
/// ("# [workers.codex.sizes]"). The block runs from the header through the line BEFORE the
/// next blank/whitespace-only line (exclusive), or to end-of-file. Everything else is
/// preserved verbatim, including the document's trailing-newline style.
///
/// Deterministic and idempotent: re-running <see cref="ReplaceCodexSizesBlock"/> with the
/// same newBlock yields the same text. When no codex-sizes header is found the input is
/// returned unchanged; the caller decides whether that is an error.
/// </summary>
public static class CodexSizesBlockEditor
{
    /// <summary>
    /// Replaces the [workers.codex.sizes] block with <paramref name="newBlock"/> and returns
    /// the edited text. Returns the input unchanged when no codex-sizes header is present.
    /// </summary>
    public static string ReplaceCodexSizesBlock(string configText, string newBlock)
    {
        if (!TryFindCodexSizesBlock(configText, out int startLine, out int endLineExclusive))
            return configText;

        // Detect whether the source ended with a trailing newline so we can restore it; the
        // split below drops a final empty segment that a trailing "\n" would otherwise imply.
        bool hadTrailingNewline = configText.Length > 0 && configText[configText.Length - 1] == '\n';

        // Match the templates: LF lines. Splitting on '\n' keeps any '\r' attached to the
        // line content, so CRLF inputs round-trip their carriage returns on untouched lines.
        var lines = new List<string>(configText.Split('\n'));

        // The new block is authored with LF separators; splice its lines in directly.
        var newBlockLines = newBlock.Split('\n');

        var rebuilt = new List<string>(lines.Count);
        rebuilt.AddRange(lines.GetRange(0, startLine));
        rebuilt.AddRange(newBlockLines);
        rebuilt.AddRange(lines.GetRange(endLineExclusive, lines.Count - endLineExclusive));

        var joined = string.Join("\n", rebuilt);

        // string.Split on a trailing '\n' produces a final empty segment, which the rejoin
        // turns back into the trailing newline - so no extra handling is needed. The guard
        // below only matters for inputs that did NOT end in '\n' but somehow gained one.
        if (!hadTrailingNewline && joined.EndsWith("\n"))
            joined = joined.Substring(0, joined.Length - 1);

        return joined;
    }

    /// <summary>
    /// Locates the [workers.codex.sizes] block. <paramref name="startLine"/> is the header
    /// line index; <paramref name="endLineExclusive"/> is the first subsequent blank/
    /// whitespace-only line index (exclusive) or the line count when none follows. Returns
    /// false when no header is found.
    /// </summary>
    public static bool TryFindCodexSizesBlock(string configText, out int startLine, out int endLineExclusive)
    {
        startLine = -1;
        endLineExclusive = -1;

        var lines = configText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsCodexSizesHeader(lines[i]))
            {
                startLine = i;
                break;
            }
        }

        if (startLine < 0)
            return false;

        int end = startLine + 1;
        while (end < lines.Length && !IsBlank(lines[end]))
            end++;

        endLineExclusive = end;
        return true;
    }

    // A header line whose content - after stripping any leading "#" markers and whitespace -
    // is exactly "[workers.codex.sizes]". Tolerates a trailing '\r' on CRLF inputs.
    private static bool IsCodexSizesHeader(string line)
    {
        var trimmed = line.TrimEnd('\r').Trim();
        while (trimmed.StartsWith("#"))
            trimmed = trimmed.Substring(1).TrimStart();
        return trimmed == "[workers.codex.sizes]";
    }

    // Blank or whitespace-only (a lone '\r' on CRLF inputs counts as blank).
    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);
}
