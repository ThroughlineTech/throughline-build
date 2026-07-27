using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Gemini;
using Xunit;

namespace ThroughlineBuild.Workers.Gemini.Tests;

public class GeminiAgentStubTests
{
    [Fact]
    public void GeminiAgent_Name_IsGemini()
    {
        var agent = new GeminiAgent();
        Assert.Equal("gemini", agent.Name);
    }

    [Fact]
    public void GeminiAgent_Digester_IsNonNull()
    {
        var agent = new GeminiAgent();
        Assert.NotNull(agent.Digester);
    }

    [Fact]
    public void ParseJsonOutput_PreservesFencedBlocks()
    {
        var stdout = """
            {"response":"<<<REVIEW_CRITIQUE_START\napply the feedback\n<<<REVIEW_CRITIQUE_END\n\nWORKER_RESULT\n{\"status\":\"Ok\",\"summary\":\"review complete\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{\"verdict\":\"Rework\",\"rationale_ref\":\"REVIEW_CRITIQUE\",\"checks_failed\":[]}}","stats":{"tokens":{"total":123},"tools":null}}
            """;

        var result = GeminiAgent.ParseJsonOutput(stdout, 0, "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Metadata.TryGetValue("rationale_ref", out var rationaleRefObj));
        var rationaleRef = Assert.IsType<System.Text.Json.JsonElement>(rationaleRefObj);
        Assert.Equal("REVIEW_CRITIQUE", rationaleRef.GetString());
        Assert.NotNull(result.Blocks);
        Assert.True(result.Blocks.ContainsKey("REVIEW_CRITIQUE"));
        Assert.Equal("apply the feedback", result.Blocks["REVIEW_CRITIQUE"]);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsTrue_IncludesYolo()
    {
        var options = new GeminiOptions { BypassPermissions = true };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = GeminiAgent.BuildArgs("the brief", options, workerOptions);

        Assert.Contains("--yolo", args);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsFalse_OmitsYolo()
    {
        var options = new GeminiOptions { BypassPermissions = false };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = GeminiAgent.BuildArgs("the brief", options, workerOptions);

        Assert.DoesNotContain("--yolo", args);
    }
}
