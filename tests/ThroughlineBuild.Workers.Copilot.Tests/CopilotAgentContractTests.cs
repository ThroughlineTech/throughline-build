using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common.Tests;

namespace ThroughlineBuild.Workers.Copilot.Tests;

// Partial-usage note: Copilot always reports zero input/output tokens (token counts
// are not available in silent mode). The contract base makes no usage assertions,
// so no special override is needed here.
public class CopilotAgentContractTests : IWorkerAgentContractTests
{
    protected override IWorkerAgent CreateAgent() => new CopilotAgent();

    protected override string KnownGoodFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "copilot-stdout-ok.txt");

    protected override string KnownErrorFixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "copilot-stdout-no-result.txt");

    protected override WorkerResult ParseFromFixtureContent(IWorkerAgent agent, string content)
        => CopilotAgent.ParseStdoutForWorkerResult(content, exitCode: 0, stderr: string.Empty);
}
