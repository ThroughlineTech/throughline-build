using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Best-guess heuristic that maps a Codex model discovery onto small/medium/large
/// <see cref="ModelTier"/> entries. Pure, no IO. The mapping is a starting point an
/// operator can hand-tune in config - it is NOT an authoritative tiering and makes no
/// claim about relative model capability beyond the rough ordering below.
///
/// Heuristic (documented here as a best guess):
///   - M = discovery.Models (payload order). Empty -> null.
///   - minis = models whose slug contains "mini" or "nano" (case-insensitive).
///   - mains = models NOT in minis; if mains is empty, mains = M.
///   - 'codex debug models' lists models most-capable-first, so mains[0] is the strongest.
///   - largeModel  = first main (most capable).
///   - mediumModel = second main when mains has >= 2 entries, else the first main.
///   - smallModel  = first mini, else the last main (least capable).
///   - small/medium efforts = each model's DefaultEffort (may be null).
///   - large effort = "escalated": the largeModel supported effort with the highest
///     rank (minimal&lt;low&lt;medium&lt;high&lt;xhigh); when SupportedEfforts is empty,
///     fall back to largeModel.DefaultEffort (may be null).
///   - Empty-string effort is treated as null.
/// </summary>
public static class CodexTierMapper
{
    // Effort rank for the large-tier escalation. Anything unrecognized ranks below "minimal".
    private static int EffortRank(string effort) => effort switch
    {
        "minimal" => 0,
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "xhigh" => 4,
        _ => -1,
    };

    /// <summary>
    /// Returns a small/medium/large -&gt; <see cref="ModelTier"/> mapping, or null when the
    /// discovery has no models.
    /// </summary>
    public static IReadOnlyDictionary<WorkerSize, ModelTier>? Map(CodexModelDiscovery discovery)
    {
        var models = discovery.Models;
        if (models.Count == 0)
            return null;

        var minis = new List<CodexModelInfo>();
        var mains = new List<CodexModelInfo>();
        foreach (var model in models)
        {
            bool isMini = model.Slug.Contains("mini", StringComparison.OrdinalIgnoreCase)
                       || model.Slug.Contains("nano", StringComparison.OrdinalIgnoreCase);
            if (isMini)
                minis.Add(model);
            else
                mains.Add(model);
        }

        // If everything looked like a mini, treat the whole list as the "main" pool.
        if (mains.Count == 0)
            mains = new List<CodexModelInfo>(models);

        // Payload is most-capable-first: mains[0] is the strongest model.
        var largeModel = mains[0];
        var mediumModel = mains.Count >= 2 ? mains[1] : mains[0];
        var smallModel = minis.Count > 0 ? minis[0] : mains[mains.Count - 1];

        var small = new ModelTier(smallModel.Slug, NormalizeEffort(smallModel.DefaultEffort));
        var medium = new ModelTier(mediumModel.Slug, NormalizeEffort(mediumModel.DefaultEffort));
        var large = new ModelTier(largeModel.Slug, EscalatedEffort(largeModel));

        return new Dictionary<WorkerSize, ModelTier>
        {
            [WorkerSize.Small] = small,
            [WorkerSize.Medium] = medium,
            [WorkerSize.Large] = large,
        };
    }

    // The highest-ranked supported effort for the large tier, or the default effort when
    // no supported efforts are listed. Returns null when neither is usable.
    private static string? EscalatedEffort(CodexModelInfo model)
    {
        if (model.SupportedEfforts.Count == 0)
            return NormalizeEffort(model.DefaultEffort);

        string? best = null;
        int bestRank = int.MinValue;
        foreach (var effort in model.SupportedEfforts)
        {
            var normalized = NormalizeEffort(effort);
            if (normalized is null)
                continue;
            int rank = EffortRank(normalized);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = normalized;
            }
        }

        // All supported entries were empty/whitespace: fall back to the default.
        return best ?? NormalizeEffort(model.DefaultEffort);
    }

    // Treat empty-string (and null) effort as null so renderers omit the effort key.
    private static string? NormalizeEffort(string? effort)
        => string.IsNullOrEmpty(effort) ? null : effort;
}
