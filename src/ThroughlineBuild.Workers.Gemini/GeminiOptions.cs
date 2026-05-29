using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiOptions
{
    public string ExecutablePath { get; init; } = "gemini";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, string> Sizes { get; init; } =
        new Dictionary<WorkerSize, string>
        {
            { WorkerSize.Small,  "gemini-2.0-flash" },
            { WorkerSize.Medium, "gemini-2.5-flash" },
            { WorkerSize.Large,  "gemini-2.5-pro"   },
        };
    // When true (default), pass --yolo to the gemini CLI so the headless run
    // does not block on the interactive approval gate. Set false from config
    // to opt back into the gate.
    public bool BypassPermissions { get; init; } = true;
}
