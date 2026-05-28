using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Codex;

public class CodexAgent : IWorkerAgent
{
    private readonly CodexOptions _options;

    public CodexAgent(CodexOptions options) => _options = options;
    public CodexAgent() : this(new CodexOptions()) { }

    public string Name => "codex";
    public IWorkerProgressDigester? Digester => null;

    public Task<WorkerResult> ExecuteAsync(Brief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        => throw new NotImplementedException("TLB-206");
}
