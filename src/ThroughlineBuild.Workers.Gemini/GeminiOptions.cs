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
}
