using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common.Tests;

namespace ThroughlineBuild.Workers.Gemini.Tests;

public class GeminiAgentContractTests : IWorkerAgentContractTests
{
    protected override IWorkerAgent CreateAgent() => new GeminiAgent();

    protected override string KnownGoodFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "gemini-json-ok.json");

    protected override string KnownErrorFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "gemini-json-no-result.json");

    protected override WorkerResult ParseFromFixtureContent(IWorkerAgent agent, string content)
        => GeminiAgent.ParseJsonOutput(content, exitCode: 0, stderr: string.Empty);
}
