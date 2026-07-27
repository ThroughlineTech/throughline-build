using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiOptions
{
    public string ExecutablePath { get; init; } = "gemini";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, ModelTier> Sizes { get; init; } =
        new Dictionary<WorkerSize, ModelTier>();
    // When true (default), pass --yolo to the gemini CLI so the headless run
    // does not block on the interactive approval gate. Set false from config
    // to opt back into the gate.
    public bool BypassPermissions { get; init; } = true;
}
