using System.Collections.Generic;
using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Copilot;

public class CopilotAgent : IWorkerAgent
{
    private readonly CopilotOptions _options;

    public CopilotAgent(CopilotOptions options) => _options = options;
    public CopilotAgent() : this(new CopilotOptions()) { }

    public string Name => "copilot";
    public IWorkerProgressDigester? Digester => null;

    public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        throw new NotImplementedException("CopilotAgent.ExecuteAsync is not yet implemented (A.03).");
    }

    // Strips the optional "github:" vendor prefix from a configured model id so the
    // bare id can be passed to `copilot --model`. Returns null when the configured
    // value is null/empty so callers can skip the flag and let copilot use its default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string githubPrefix = "github:";
        if (trimmed.StartsWith(githubPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(githubPrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Applies user-supplied EnvironmentVariables to the child process environment.
    // NOTE: Unlike other worker agents (ClaudeCode, Codex, Gemini), Copilot auth
    // is additive (set GH_TOKEN), not subtractive (strip key). The caller passes
    // GH_TOKEN via options.EnvironmentVariables when explicit auth is needed;
    // otherwise the gh keyring credential is inherited from the parent process.
    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Builds llm_usage metadata for Copilot runs. vendor is always "github".
    // cost_usd is always null (Copilot bills in premium-request quota, not USD).
    // Token counts are not available in silent mode (-s); both are 0.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(long wallClockMs, string? model)
    {
        return new Dictionary<string, object>
        {
            { "vendor",        "github" },
            { "model",         (object)(model ?? "") },
            { "wall_clock_ms", wallClockMs },
            { "input_tokens",  (object)0 },
            { "output_tokens", (object)0 },
            { "cost_usd",      (object?)null! },
        };
    }
}
