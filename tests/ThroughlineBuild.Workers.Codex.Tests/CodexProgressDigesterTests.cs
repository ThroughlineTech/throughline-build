using System.Text.Json;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexProgressDigesterTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FormatLine_ThreadStarted_ProducesSessionDigestLine()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"thread.started","thread_id":"019e8e73-30f0-7ab1-ba36-daedaae11bcf"}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("session", result);
        Assert.Contains("019e8e73", result);
    }

    [Fact]
    public void FormatLine_ItemStartedCommandExecution_SurfacesCommand()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"pwsh -Command rg progress","aggregated_output":"","exit_code":null,"status":"in_progress"}}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("tool_start", result);
        Assert.Contains("rg progress", result);
    }

    [Fact]
    public void FormatLine_ItemCompletedCommandExecution_SurfacesExitCode()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"pwsh -Command dotnet test","aggregated_output":"ok","exit_code":0,"status":"completed"}}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("tool_done", result);
        Assert.Contains("exit 0", result);
    }

    [Fact]
    public void FormatLine_ItemCompletedAgentMessage_SurfacesMessageExcerpt()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"WORKER_RESULT\n{\"status\":\"Ok\"}"}}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("message", result);
        Assert.Contains("WORKER_RESULT", result);
    }

    [Fact]
    public void FormatLine_TurnCompleted_SurfacesUsage()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":12,"reasoning_output_tokens":3}}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("turn_done", result);
        Assert.Contains("100 in / 12 out", result);
    }

    [Fact]
    public void FormatActivity_CommandExecution_ReturnsLastActivitySummary()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"pwsh -Command rg ProgressDigestSink","aggregated_output":"","exit_code":null,"status":"in_progress"}}""";
        var result = d.FormatActivity(json);
        Assert.NotNull(result);
        Assert.Contains("in_progress", result);
        Assert.Contains("rg ProgressDigestSink", result);
    }

    [Fact]
    public void FormatLine_UnknownType_ReturnsNull()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"unknown_event","data":"whatever"}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_MalformedJson_ReturnsNull()
    {
        var d = new CodexProgressDigester();
        var result = d.FormatLine("{not valid json}");
        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_EmptyString_ReturnsNull()
    {
        var d = new CodexProgressDigester();
        var result = d.FormatLine("");
        Assert.Null(result);
    }

    [Fact]
    public void Digester_OnNewAgent_IsNonNull()
    {
        var agent = new CodexAgent();
        Assert.NotNull(agent.Digester);
    }
}
