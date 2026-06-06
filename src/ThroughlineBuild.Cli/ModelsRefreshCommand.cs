using System.Text;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements 'build models refresh': re-probe Codex, compute the proposed tier mapping,
/// print a current-to-proposed diff, and overwrite ONLY the [workers.codex.sizes] block
/// (and its discovered-menu comment) in place. Every other section - the Claude block,
/// ticketing, ship, etc. - is preserved verbatim.
///
/// Refresh is deliberately not regenerate-idempotent across days: Codex's lineup changes,
/// so the diff is the safety surface. A probe failure stops with an actionable message and
/// leaves the config byte-unchanged. Dispatched in the early pre-config-load band (like
/// 'build init') because it manipulates the config file rather than consuming it.
/// </summary>
public static class ModelsRefreshCommand
{
    /// <param name="cwd">Directory to search upward from for .build/config.toml.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <param name="probeCodex">Codex probe (production wires the real probe; tests inject stubs).</param>
    /// <returns>0 on success (rewrote or already up to date); 1 on probe/IO failure; 2 when no config exists.</returns>
    public static int Execute(string cwd, IConsole console, Func<CodexProbeResult> probeCodex)
    {
        var configPath = BuildConfigLoader.FindConfigFile(cwd);
        if (configPath is null)
        {
            console.ErrorWriteLine(
                "Error: no .build/config.toml found (searched upward from the current directory). Run 'build init' first.");
            return 2;
        }

        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(configPath);
        }
        catch (IOException ex)
        {
            console.ErrorWriteLine($"Error: could not read {configPath}: {ex.Message}");
            return 1;
        }

        // Preserve the file's leading BOM (if any) so a rewrite touches only the codex block.
        bool hadBom = originalBytes.Length >= 3
            && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
        var originalText = new UTF8Encoding(false).GetString(
            hadBom ? originalBytes.AsSpan(3) : originalBytes);

        if (!CodexSizesBlockEditor.TryFindCodexSizesBlock(originalText, out int startLine, out int endExclusive))
        {
            console.ErrorWriteLine(
                $"Error: no [workers.codex.sizes] block found in {configPath}; nothing to refresh.");
            return 1;
        }

        // Probe BEFORE any write decision. On failure: actionable message, file untouched.
        var probe = probeCodex();
        if (!probe.Success || probe.Discovery is null || probe.Discovery.Models.Count == 0)
        {
            var detail = probe.Diagnostic ?? "no models returned";
            console.ErrorWriteLine(
                $"Error: could not query Codex models ({detail}). Codex may not be installed or not logged in. "
                + "The config was left unchanged.");
            return 1;
        }

        var mapping = CodexTierMapper.Map(probe.Discovery);
        if (mapping is null)
        {
            console.ErrorWriteLine("Error: Codex returned no usable models; the config was left unchanged.");
            return 1;
        }

        // Render the proposed block in the same active/commented form as the current one,
        // so refresh never silently activates or comments out the codex block.
        var lines = originalText.Split('\n');
        bool commented = IsCommentedHeader(lines[startLine]);
        var proposedBlock = CodexSizesBlockRenderer.Render(mapping, probe.Discovery, commented);

        var currentBlock = string.Join("\n", lines[startLine..endExclusive]);
        if (string.Equals(currentBlock, proposedBlock, StringComparison.Ordinal))
        {
            console.WriteLine($"Codex tiers in {configPath} are already up to date; no change.");
            return 0;
        }

        PrintDiff(console, configPath, CodexSizesBlockReader.Read(originalText), mapping);

        var newText = CodexSizesBlockEditor.ReplaceCodexSizesBlock(originalText, proposedBlock);
        try
        {
            File.WriteAllText(configPath, newText, new UTF8Encoding(hadBom));
        }
        catch (IOException ex)
        {
            console.ErrorWriteLine($"Error: could not write {configPath}: {ex.Message}");
            return 1;
        }

        console.WriteLine($"Rewrote [workers.codex.sizes] in {configPath}.");
        return 0;
    }

    private static bool IsCommentedHeader(string headerLine)
        => headerLine.TrimStart().StartsWith("#");

    private static void PrintDiff(
        IConsole console,
        string configPath,
        IReadOnlyDictionary<WorkerSize, ModelTier>? current,
        IReadOnlyDictionary<WorkerSize, ModelTier> proposed)
    {
        console.WriteLine($"Codex tier changes for {configPath}:");
        console.WriteLine($"  {"tier",-8}{"current",-34}proposed");
        foreach (var (label, size) in new[]
                 {
                     ("small", WorkerSize.Small),
                     ("medium", WorkerSize.Medium),
                     ("large", WorkerSize.Large),
                 })
        {
            var cur = current is not null && current.TryGetValue(size, out var c) ? Format(c) : "(unset)";
            var prop = proposed.TryGetValue(size, out var p) ? Format(p) : "(unset)";
            console.WriteLine($"  {label,-8}{cur,-34}{prop}");
        }
    }

    // "model" or "model / effort" for a tier cell.
    private static string Format(ModelTier tier)
        => string.IsNullOrEmpty(tier.Effort) ? tier.Model : $"{tier.Model} / {tier.Effort}";
}
