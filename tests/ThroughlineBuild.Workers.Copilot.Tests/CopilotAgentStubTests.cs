using System.Collections.Generic;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Copilot;
using Xunit;

namespace ThroughlineBuild.Workers.Copilot.Tests;

public class CopilotAgentTests
{
    [Fact]
    public void Name_ReturnsCopilot()
    {
        var agent = new CopilotAgent();
        Assert.Equal("copilot", agent.Name);
    }

    [Fact]
    public void Digester_ReturnsNull()
    {
        var agent = new CopilotAgent();
        Assert.Null(agent.Digester);
    }

    [Fact]
    public void Options_DefaultSizesHasThreeEntries()
    {
        var opts = new CopilotOptions();
        Assert.Equal(3, opts.Sizes.Count);
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Small));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Medium));
        Assert.True(opts.Sizes.ContainsKey(WorkerSize.Large));
    }

    [Fact]
    public void ParseStdoutForWorkerResult_ValidWorkerResult_ReturnsOk()
    {
        var stdout = """
            Some copilot output here.
            WORKER_RESULT
            {
              "status": "Ok",
              "summary": "Did the thing",
              "files_changed": ["foo.cs"],
              "failure_reason": null,
              "metadata": {}
            }
            """;

        var result = CopilotAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("Did the thing", result.Summary);
        Assert.Contains("foo.cs", result.FilesChanged);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NoMarker_ReturnsFailed()
    {
        var result = CopilotAgent.ParseStdoutForWorkerResult("no marker here", 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NonZeroExit_ReturnsFailed()
    {
        var result = CopilotAgent.ParseStdoutForWorkerResult("", 1, "some error");
        Assert.Equal(Status.Failed, result.Status);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_InvalidJson_ReturnsFailed()
    {
        var stdout = "WORKER_RESULT\n{not valid json}";
        var result = CopilotAgent.ParseStdoutForWorkerResult(stdout, 0, "");
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_NeedsRework_ReturnsNeedsRework()
    {
        var stdout = """
            WORKER_RESULT
            {
              "status": "NeedsRework",
              "summary": "Needs another pass",
              "files_changed": [],
              "failure_reason": "missing tests",
              "metadata": {}
            }
            """;

        var result = CopilotAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        Assert.Equal(Status.NeedsRework, result.Status);
        Assert.Equal("Needs another pass", result.Summary);
        Assert.Equal("missing tests", result.FailureReason);
    }

    [Fact]
    public void ParseStdoutForWorkerResult_ValidResult_HasEmptyMetadata()
    {
        var stdout = """
            WORKER_RESULT
            {
              "status": "Ok",
              "summary": "Done",
              "files_changed": [],
              "failure_reason": null,
              "metadata": {}
            }
            """;

        var result = CopilotAgent.ParseStdoutForWorkerResult(stdout, 0, "");

        // ParseStdoutForWorkerResult resets metadata to empty dict
        Assert.Empty(result.Metadata);
    }

    [Fact]
    public void NormalizeModel_Null_ReturnsNull()
    {
        Assert.Null(CopilotAgent.NormalizeModel(null));
    }

    [Fact]
    public void NormalizeModel_Empty_ReturnsNull()
    {
        Assert.Null(CopilotAgent.NormalizeModel(""));
        Assert.Null(CopilotAgent.NormalizeModel("   "));
    }

    [Fact]
    public void NormalizeModel_StripGithubPrefix()
    {
        Assert.Equal("claude-3.5-sonnet", CopilotAgent.NormalizeModel("github:claude-3.5-sonnet"));
        Assert.Equal("claude-3.5-sonnet", CopilotAgent.NormalizeModel("GITHUB:claude-3.5-sonnet"));
    }

    [Fact]
    public void NormalizeModel_NoPrefix_ReturnsBareId()
    {
        Assert.Equal("gpt-4o", CopilotAgent.NormalizeModel("gpt-4o"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_VendorIsGithub()
    {
        var meta = CopilotAgent.BuildLlmUsageMetadata(1234L, "gpt-4o");
        Assert.Equal("github", meta["vendor"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_TokensAreZero()
    {
        var meta = CopilotAgent.BuildLlmUsageMetadata(1234L, null);
        Assert.Equal((object)0, meta["input_tokens"]);
        Assert.Equal((object)0, meta["output_tokens"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WallClockIsPreserved()
    {
        var meta = CopilotAgent.BuildLlmUsageMetadata(5678L, "claude-3.5-sonnet");
        Assert.Equal(5678L, meta["wall_clock_ms"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_NullModel_UsesEmptyString()
    {
        var meta = CopilotAgent.BuildLlmUsageMetadata(0L, null);
        Assert.Equal("", meta["model"]);
    }
}
