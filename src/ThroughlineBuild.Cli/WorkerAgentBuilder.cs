using ThroughlineBuild.Contracts;
using ThroughlineBuild.Workers.ClaudeCode;
using ThroughlineBuild.Workers.Codex;
using ThroughlineBuild.Workers.Copilot;
using ThroughlineBuild.Workers.Gemini;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Constructs an <see cref="IWorkerAgent"/> from an <see cref="AgentConfig"/> by agent name. The
/// name -> concrete-agent mapping lives here so both the phase-verb factory wiring and the scaffold
/// profile-derivation path build agents the same way.
/// </summary>
public static class WorkerAgentBuilder
{
    public static IWorkerAgent Create(string name, AgentConfig cfg) => name switch
    {
        "gemini" => new GeminiAgent(new GeminiOptions
        {
            ExecutablePath = cfg.Executable,
            MaxOutputTokens = cfg.MaxOutputTokens,
            Sizes = cfg.Sizes,
            BypassPermissions = cfg.BypassPermissions,
        }),
        "codex" => new CodexAgent(new CodexOptions
        {
            ExecutablePath = cfg.Executable,
            MaxOutputTokens = cfg.MaxOutputTokens,
            Sizes = cfg.Sizes,
            BypassPermissions = cfg.BypassPermissions,
        }),
        "copilot" => new CopilotAgent(new CopilotOptions
        {
            ExecutablePath = cfg.Executable,
            MaxOutputTokens = cfg.MaxOutputTokens,
            Sizes = cfg.Sizes,
        }),
        _ => new ClaudeCodeAgent(new ClaudeCodeOptions
        {
            ExecutablePath = cfg.Executable,
            MaxOutputTokens = cfg.MaxOutputTokens,
            Sizes = cfg.Sizes,
            BypassPermissions = cfg.BypassPermissions,
            Transport = cfg.Transport,
        }),
    };
}
