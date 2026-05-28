using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Gemini;

public class GeminiAgent : IWorkerAgent
{
    private readonly GeminiOptions _options;

    public GeminiAgent(GeminiOptions options) => _options = options;
    public GeminiAgent() : this(new GeminiOptions()) { }

    public string Name => "gemini";
    public IWorkerProgressDigester? Digester => null;

    public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
    {
        throw new NotImplementedException("GeminiAgent.ExecuteAsync is not yet implemented (B02).");
    }
}
