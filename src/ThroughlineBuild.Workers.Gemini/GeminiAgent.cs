using System.Collections.Generic;
using System.Diagnostics;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiAgent : IWorkerAgent
{
    private readonly GeminiOptions _options;
    private readonly GeminiProgressDigester _digester = new();

    public GeminiAgent(GeminiOptions options) => _options = options;
    public GeminiAgent() : this(new GeminiOptions()) { }

    public string Name => "gemini";
    public IWorkerProgressDigester? Digester => _digester;

    public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        // TODO(TLB-219): wire NormalizeModel, ConfigureEnvironment, BuildLlmUsageMetadata here when TLB-219 lands.
        throw new NotImplementedException("GeminiAgent.ExecuteAsync is not yet implemented (B02).");
    }

    // Strips the optional "google:" vendor prefix from a configured model id so the
    // bare id (e.g. "gemini-2.5-pro") can be passed to `gemini --model`. Returns
    // null when the configured value is null/empty or prefix-only so callers can
    // skip emitting the flag and let the Gemini CLI use its own default.
    internal static string? NormalizeModel(string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return null;
        var trimmed = configuredModel.Trim();
        const string googlePrefix = "google:";
        if (trimmed.StartsWith(googlePrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(googlePrefix.Length);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Removes GEMINI_API_KEY and GOOGLE_API_KEY from the child process environment
    // to force OAuth / subscription auth. The Gemini CLI will fall back to the
    // user's ADC (Application Default Credentials) or gcloud login when neither
    // API-key variable is present.
    //
    // Note on MaxOutputTokens: the Gemini CLI headless mode does not expose an
    // output-token-cap environment variable equivalent to CLAUDE_CODE_MAX_OUTPUT_TOKENS.
    // _options.MaxOutputTokens is reserved for future use once the CLI supports it.
    //
    // User-supplied EnvironmentVariables are applied after the strip so an explicit
    // user override still wins.
    internal void ConfigureEnvironment(ProcessStartInfo psi, WorkerOptions options)
    {
        psi.Environment.Remove("GEMINI_API_KEY");
        psi.Environment.Remove("GOOGLE_API_KEY");
        if (options.EnvironmentVariables != null)
            foreach (var (k, v) in options.EnvironmentVariables)
                psi.Environment[k] = v;
    }

    // Builds the llm_usage metadata dictionary for Gemini runs.
    // vendor is always "google". cost_usd is always null (Gemini CLI does not
    // report USD cost). input_tokens is sourced from usage.Tokens.Total (the
    // Gemini CLI reports a combined token total, not a split); output_tokens is
    // 0 as a placeholder until the CLI exposes a separate output-token field.
    internal static Dictionary<string, object> BuildLlmUsageMetadata(
        GeminiUsage? usage, long wallClockMs, string? model)
    {
        var metadata = new Dictionary<string, object>
        {
            { "vendor",        "google" },
            { "model",         (object)(model ?? "") },
            { "wall_clock_ms", wallClockMs },
            { "input_tokens",  (object)(usage?.Tokens?.Total ?? 0) },
            { "output_tokens", (object)0 },
            { "cost_usd",      (object?)null! },
        };
        return metadata;
    }
}
