using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Codex;

public class CodexOptions
{
    public string ExecutablePath { get; init; } = "codex";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, string> Sizes { get; init; } =
        new Dictionary<WorkerSize, string>
        {
            { WorkerSize.Small,  "gpt-5.4-mini" },
            { WorkerSize.Medium, "gpt-5.3-codex" },
            { WorkerSize.Large,  "gpt-5.5" },
        };
    // When true (default), pass --full-auto to the codex CLI so the headless
    // run does not block on the interactive approval gate. Set false from
    // config to opt back into the gate.
    public bool BypassPermissions { get; init; } = true;
}
