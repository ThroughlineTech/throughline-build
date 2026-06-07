namespace ThroughlineBuild.Cli;

/// <summary>
/// Honesty check for the verifier tool allowlist. verifier_allowed_tools is only enforced by
/// workers that pass a per-tool allowlist to their CLI (claude-code, copilot). codex and gemini
/// ignore it and run the verifier unsandboxed, so a configured allowlist gives a false sense of
/// safety. This surfaces that gap as a one-line startup warning. See TLB-478.
/// </summary>
public static class VerifierToolEnforcement
{
    // Workers that actually forward an allowlist to their CLI (ClaudeCodeAgent --allowedTools,
    // CopilotAgent per-tool flags). codex/gemini ignore WorkerOptions.AllowedTools entirely.
    private static readonly HashSet<string> EnforcingAgents =
        new(StringComparer.OrdinalIgnoreCase) { "claude-code", "copilot" };

    /// <summary>
    /// Returns a warning string when verifier_allowed_tools is configured but the resolved
    /// review worker will not enforce it; returns null when there is nothing to warn about.
    /// </summary>
    public static string? UnenforcedWarning(string reviewAgentName, IReadOnlyList<string> allowedTools)
    {
        if (allowedTools is null || allowedTools.Count == 0)
            return null;
        if (EnforcingAgents.Contains(reviewAgentName))
            return null;
        return $"warning: verifier_allowed_tools is set ([{string.Join(", ", allowedTools)}]) but the review " +
               $"worker '{reviewAgentName}' does not honor a tool allowlist - the verifier runs unsandboxed. " +
               $"Review safety relies on the prompt and the post-review git-state guard (TLB-478).";
    }
}
