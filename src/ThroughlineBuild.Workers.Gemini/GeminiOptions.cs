using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiOptions
{
    public string ExecutablePath { get; init; } = "gemini";
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public int? MaxOutputTokens { get; init; } = null;
    public IReadOnlyDictionary<WorkerSize, string> Sizes { get; init; } =
        new Dictionary<WorkerSize, string>();
}
