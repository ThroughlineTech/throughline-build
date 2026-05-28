using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common.Tests;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexAgentContractTests : IWorkerAgentContractTests
{
    protected override IWorkerAgent CreateAgent() => new CodexAgent();

    protected override string KnownGoodFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "codex-stdout-worker-result-ok.txt");

    protected override string KnownErrorFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "codex-stdout-no-result.txt");

    protected override WorkerResult ParseFromFixtureContent(IWorkerAgent agent, string content)
        => CodexAgent.ParseStdoutForWorkerResult(content, exitCode: 0, stderr: string.Empty);
}
