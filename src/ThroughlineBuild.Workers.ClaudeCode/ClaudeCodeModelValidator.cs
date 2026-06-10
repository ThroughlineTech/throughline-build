namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Validates a configured [workers.claude-code.sizes] model value before any worker
/// spawns. The Claude Code CLI accepts only the tier aliases (haiku/sonnet/opus) or a
/// full model name (claude-*); anything else fails at session init with a generic
/// "issue with the selected model" envelope error deep inside a chain run. Validating
/// at config load turns that into an actionable "Config error:" at startup. The
/// canonical trigger is `model = "fable"` - Fable has no CLI tier alias and must be
/// configured by full slug (claude-fable-5).
/// </summary>
public static class ClaudeCodeModelValidator
{
    private static readonly string[] TierAliases = { "haiku", "sonnet", "opus" };

    /// <summary>
    /// Returns null when <paramref name="configuredModel"/> is a value the Claude Code
    /// CLI can resolve, otherwise an operator-actionable reason. An optional
    /// "anthropic:" provider prefix is accepted (NormalizeModel strips it at spawn).
    /// Null/empty values return null - the config loader already rejects those.
    /// </summary>
    public static string? Validate(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;

        var model = configuredModel.Trim();
        const string anthropicPrefix = "anthropic:";
        if (model.StartsWith(anthropicPrefix, StringComparison.OrdinalIgnoreCase))
            model = model.Substring(anthropicPrefix.Length).Trim();

        foreach (var alias in TierAliases)
        {
            if (string.Equals(model, alias, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(model, "fable", StringComparison.OrdinalIgnoreCase))
            return "'fable' is not a Claude Code tier alias (only haiku, sonnet, and opus auto-track); " +
                   "use the full slug, e.g. { model = \"claude-fable-5\" }";

        return $"'{model}' is not a value the Claude Code CLI can resolve; use a tier alias " +
               "(haiku, sonnet, opus) or a full claude-* model id (e.g. \"claude-fable-5\")";
    }
}
