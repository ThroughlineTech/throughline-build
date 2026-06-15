using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Operator-facing front end for <see cref="ClaudeCodePreflight"/>. Two entry points:
/// <list type="bullet">
/// <item><see cref="ReportAsync"/> - the <c>build setup</c> diagnostic: prints each Claude agent's
/// transport and capability, and returns a non-zero exit when any interactive-hook agent is
/// unsupported, so an operator learns about an unusable transport before running a phase.</item>
/// <item><see cref="GateAsync"/> - the phase-dispatch guard: checks only the agents a verb will run
/// and fails clearly BEFORE the phase starts when interactive-hook cannot be supported. Selecting
/// interactive-hook never silently falls back to print.</item>
/// </list>
/// </summary>
public static class ClaudeTransportPreflight
{
    // Names mapped to a non-Claude worker by WorkerAgentBuilder.Create; every other name maps to
    // ClaudeCodeAgent and therefore carries the Claude transport. Config.cs parses `transport` for any
    // such Claude-family name (not just the literal "claude-code" block), so this same predicate gates
    // which agents the preflight applies to.
    private static bool IsClaudeAgent(string name) => name is not ("gemini" or "codex" or "copilot");

    /// <summary>
    /// The phase names a phase verb will run a worker for, so the dispatch gate checks the right
    /// agents. Plan is gated only in investigate mode - promote and <c>--from-brief</c> spawn no worker,
    /// so gating them would falsely require claude for a deterministic plan. Ship and any non-phase verb
    /// return empty. Chain's <c>implement</c> entry also covers the batch-implement worker (built from
    /// the implement-phase agent). The draft (<c>build new</c>) and scaffold paths gate at their own
    /// dispatch sites and are not routed here.
    /// </summary>
    public static IReadOnlyList<string> PhasesToGateForVerb(string verb, bool planIsPromote, bool fromBrief) => verb switch
    {
        "implement" => new[] { "implement" },
        "review" => new[] { "review" },
        "rework" => new[] { "implement" },
        "decompose" => new[] { "decompose" },
        "chain" => new[] { "implement", "review" },
        "plan" => (!fromBrief && !planIsPromote) ? new[] { "plan" } : Array.Empty<string>(),
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// <c>build setup</c> diagnostic. Reports every configured Claude agent's transport and whether
    /// the host can support it. Returns 0 when all are supported (or print), non-zero when an
    /// interactive-hook agent is unsupported.
    /// </summary>
    public static async Task<int> ReportAsync(
        WorkersConfig workers, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var claudeAgents = workers.Agents
            .Where(kv => IsClaudeAgent(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        if (claudeAgents.Count == 0)
        {
            output.WriteLine("Claude transport preflight: no claude-code agent configured; nothing to check.");
            return 0;
        }

        output.WriteLine("Claude transport preflight:");
        int exit = 0;
        foreach (var (name, cfg) in claudeAgents)
        {
            var result = await ClaudeCodePreflight.CheckAsync(cfg.Executable, cfg.Transport, ct).ConfigureAwait(false);
            var status = result.Supported ? "OK" : "UNSUPPORTED";
            output.WriteLine($"  [{status}] agent '{name}' (executable '{cfg.Executable}'): {result.Message}");
            if (!result.Supported)
            {
                error.WriteLine($"[build] agent '{name}' cannot use its configured transport on this host.");
                exit = 2;
            }
        }
        return exit;
    }

    /// <summary>
    /// Phase-dispatch guard. For the resolved agents a verb is about to run, verifies any configured
    /// for interactive-hook can actually run here. On the first unsupported agent, writes a clear error
    /// (distinct from quota/model/permission/protocol failures) and returns a non-zero exit; otherwise 0.
    /// Print agents and non-Claude agents are skipped (no version probe, no behavior change).
    /// </summary>
    public static async Task<int> GateAsync(
        WorkersConfig workers, IEnumerable<string> resolvedAgentNames, TextWriter error, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in resolvedAgentNames)
        {
            if (!seen.Add(name)) continue;
            if (!IsClaudeAgent(name)) continue;
            if (!workers.Agents.TryGetValue(name, out var cfg)) continue;
            if (cfg.Transport != ClaudeCodeTransport.InteractiveHook) continue;

            var result = await ClaudeCodePreflight.CheckAsync(cfg.Executable, ClaudeCodeTransport.InteractiveHook, ct)
                .ConfigureAwait(false);
            if (!result.Supported)
            {
                error.WriteLine(
                    $"[build] Cannot start phase: agent '{name}' is configured with transport = \"interactive-hook\", " +
                    "which this host cannot support.");
                error.WriteLine($"[build] {result.Message}");
                return 2;
            }
        }
        return 0;
    }
}
