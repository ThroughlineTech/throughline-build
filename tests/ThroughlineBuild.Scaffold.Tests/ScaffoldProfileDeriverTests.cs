using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Scaffold;
using Xunit;
using WorkerBrief = ThroughlineBuild.Contracts.Models.Brief;

namespace ThroughlineBuild.Scaffold.Tests;

public class ScaffoldProfileDeriverTests
{
    static ScaffoldProfileDeriverTests()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    }

    private sealed class FakeWorker : IWorkerAgent
    {
        private readonly WorkerResult _result;
        public WorkerBrief? LastBrief { get; private set; }

        public FakeWorker(WorkerResult result) => _result = result;

        public string Name => "fake";
        public IWorkerProgressDigester? Digester => null;

        public Task<WorkerResult> ExecuteAsync(WorkerBrief brief, string workingDirectory, WorkerOptions options, CancellationToken ct)
        {
            LastBrief = brief;
            return Task.FromResult(_result);
        }
    }

    private static WorkerResult OkWith(string? profileBlock)
    {
        var blocks = profileBlock is null
            ? null
            : new Dictionary<string, string> { ["PROJECT_PROFILE"] = profileBlock };
        return new WorkerResult(
            Status.Ok, "derived", Array.Empty<string>(), null,
            new Dictionary<string, object>(), blocks);
    }

    private const string GoodProfile = """
    {
      "language": "typescript",
      "framework": "react-vite",
      "package_manager": "npm",
      "build_command": "npm run build",
      "test_command": "npm test",
      "review_checks": [
        { "name": "build", "executable": "npm", "arguments": ["run", "build"], "timeout_minutes": 5 },
        { "name": "test", "executable": "npm", "arguments": ["test"], "timeout_minutes": 10 }
      ]
    }
    """;

    [Fact]
    public async Task HappyPath_ReturnsParsedProfile_AndEmbedsOpDocInBrief()
    {
        var worker = new FakeWorker(OkWith(GoodProfile));
        var deriver = new ScaffoldProfileDeriver(worker);

        var result = await deriver.DeriveAsync(
            "# Operation: demo\nBuild with npm run build / npm test.",
            "/tmp", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.Profile);
        Assert.Equal("react-vite", result.Profile!.Framework);
        Assert.Equal(2, result.Profile.ReviewChecks.Count);

        // The op-doc text must be handed to the agent.
        Assert.Contains("Operation: demo", worker.LastBrief!.Instruction);
        Assert.Equal(Phase.Scaffold, worker.LastBrief.Phase);
    }

    [Fact]
    public async Task WorkerFailed_ReturnsFailure()
    {
        var failed = new WorkerResult(Status.Failed, "boom", Array.Empty<string>(), "agent crashed",
            new Dictionary<string, object>(), null);
        var deriver = new ScaffoldProfileDeriver(new FakeWorker(failed));

        var result = await deriver.DeriveAsync("op", "/tmp", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("agent crashed", result.FailureReason);
    }

    [Fact]
    public async Task MissingProfileBlock_ReturnsFailure()
    {
        var deriver = new ScaffoldProfileDeriver(new FakeWorker(OkWith(null)));

        var result = await deriver.DeriveAsync("op", "/tmp", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("PROJECT_PROFILE", result.FailureReason);
    }

    [Fact]
    public async Task InvalidProfileJson_ReturnsFailure()
    {
        var deriver = new ScaffoldProfileDeriver(new FakeWorker(OkWith("{ not valid }")));

        var result = await deriver.DeriveAsync("op", "/tmp", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("invalid", result.FailureReason);
    }

    // Prompt-contract guard: the deriver's actual classification is the LLM's (faked above), but the
    // prompt MUST instruct it to emit a gating/advisory role per check, and show the field in the
    // example. A future edit that drops the role instruction (re-introducing lint/format hard-gating)
    // fails here.
    [Fact]
    public void DeriveProfilePrompt_RequiresARolePerCheck_IncludingSetup()
    {
        var prompt = ProfilePromptLoader.Load();

        Assert.Contains("\"role\" is \"gating\", \"advisory\", or \"setup\"", prompt);
        Assert.Contains("REQUIRED on every check", prompt);
        Assert.Contains("\"role\": \"advisory\"", prompt);
    }
}
