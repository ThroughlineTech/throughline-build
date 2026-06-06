using System.Text;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Renders the full [workers.codex.sizes] block: header, a discovered-menu comment, and
/// the three size entries. Pure - no IO.
///
/// When <c>commented == true</c> every line is prefixed with "# " (a freshly-init'd config
/// keeps codex commented because default_agent is claude-code). When false the block is
/// active TOML: the header and size lines are live, while the explanatory "# ..." lines stay
/// real single-"#" comments.
///
/// The menu lists discovered models and their efforts so an operator can hand-tune without
/// guessing slugs. Output uses LF, matching the embedded template. Lines emitted (commented
/// form shown):
///   # [workers.codex.sizes]
///   # # openai: prefix is optional; CodexAgent strips it automatically.
///   # # effort is Codex-only (reasoning level: minimal | low | medium | high | xhigh).
///   # # Discovered by 'build init' via 'codex debug models'; run 'build models refresh' to update.
///   # # models: gpt-5.5, gpt-5.4-mini
///   # # effort: gpt-5.5 default=medium [minimal,low,medium,high,xhigh]; gpt-5.4-mini default=low [low,medium,high]
///   # small  = { model = "gpt-5.4-mini", effort = "low" }
///   # medium = { model = "gpt-5.5", effort = "medium" }
///   # large  = { model = "gpt-5.5", effort = "xhigh" }
/// </summary>
public static class CodexSizesBlockRenderer
{
    public static string Render(
        IReadOnlyDictionary<WorkerSize, ModelTier> mapping,
        CodexModelDiscovery discovery,
        bool commented)
    {
        // Prefix applied to active TOML lines (header + size entries): "# " when commented.
        var tomlPrefix = commented ? "# " : string.Empty;
        // Comment lines always carry a single "#"; when the whole block is commented they
        // gain a second leading "# " so the file stays valid TOML.
        var commentPrefix = commented ? "# # " : "# ";

        var lines = new List<string>
        {
            tomlPrefix + "[workers.codex.sizes]",
            commentPrefix + "openai: prefix is optional; CodexAgent strips it automatically.",
            commentPrefix + "effort is Codex-only (reasoning level: minimal | low | medium | high | xhigh).",
            commentPrefix + "Discovered by 'build init' via 'codex debug models'; run 'build models refresh' to update.",
            commentPrefix + "models: " + RenderModelsLine(discovery),
            commentPrefix + "effort: " + RenderEffortLine(discovery),
            tomlPrefix + RenderSizeEntry("small ", mapping[WorkerSize.Small]),
            tomlPrefix + RenderSizeEntry("medium", mapping[WorkerSize.Medium]),
            tomlPrefix + RenderSizeEntry("large ", mapping[WorkerSize.Large]),
        };

        return string.Join("\n", lines);
    }

    // Comma-joined discovery slugs in payload order.
    private static string RenderModelsLine(CodexModelDiscovery discovery)
        => string.Join(", ", discovery.Models.Select(m => m.Slug));

    // "<slug> default=<default-or-?> [<supported,comma>]" entries joined by "; ".
    private static string RenderEffortLine(CodexModelDiscovery discovery)
    {
        var parts = new List<string>(discovery.Models.Count);
        foreach (var model in discovery.Models)
        {
            var def = string.IsNullOrEmpty(model.DefaultEffort) ? "?" : model.DefaultEffort;
            var supported = string.Join(",", model.SupportedEfforts);
            parts.Add($"{model.Slug} default={def} [{supported}]");
        }
        return string.Join("; ", parts);
    }

    // "<paddedName> = { model = "x" }" or "... , effort = "y" }" when effort is set.
    // paddedName is already two-space aligned by the caller ("small ", "medium", "large ").
    private static string RenderSizeEntry(string paddedName, ModelTier tier)
    {
        var sb = new StringBuilder();
        sb.Append(paddedName);
        sb.Append(" = { model = \"");
        sb.Append(tier.Model);
        sb.Append('"');
        if (!string.IsNullOrEmpty(tier.Effort))
        {
            sb.Append(", effort = \"");
            sb.Append(tier.Effort);
            sb.Append('"');
        }
        sb.Append(" }");
        return sb.ToString();
    }
}
