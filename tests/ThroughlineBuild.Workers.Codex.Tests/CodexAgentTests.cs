using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexAgentTests
{
    [Fact]
    public void Name_IsCodex()
    {
        var agent = new CodexAgent();
        Assert.Equal("codex", agent.Name);
    }

    [Fact]
    public void Digester_IsNotNull()
    {
        var agent = new CodexAgent();
        Assert.NotNull(agent.Digester);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_ValidWorkerResult_ReturnsOk()
    {
        var stdout = """
            Some Codex output here.
            WORKER_RESULT
            {
              "status": "Ok",
              "summary": "Did the thing",
              "files_changed": ["foo.cs"],
              "failure_reason": null,
              "metadata": {}
            }
            """;

        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("Did the thing", result.Summary);
        Assert.Contains("foo.cs", result.FilesChanged);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NoMarker_ReturnsFailed()
    {
        var result = CodexAgent.ParseStdoutForWorkerResult("no marker here", 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NonZeroExit_ReturnsFailed()
    {
        var result = CodexAgent.ParseStdoutForWorkerResult("", 1, "some error");
        Assert.Equal(Status.Failed, result.Status);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_InvalidJson_ReturnsFailed()
    {
        var stdout = "WORKER_RESULT\n{not valid json}";
        var result = CodexAgent.ParseStdoutForWorkerResult(stdout, 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }
}
