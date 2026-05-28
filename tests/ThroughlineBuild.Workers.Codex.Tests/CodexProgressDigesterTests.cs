using System.Text.Json;
using ThroughlineBuild.Workers.Codex;
using Xunit;

namespace ThroughlineBuild.Workers.Codex.Tests;

public class CodexProgressDigesterTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FormatLine_MessageEvent_ProducesDigestLine()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"message","content":"Hello from Codex"}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("message", result);
        Assert.Contains("Hello from Codex", result);
    }

    [Fact]
    public void FormatLine_ToolCallEvent_SurfacesToolName()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"tool_call","name":"bash","arguments":{"command":"ls -la"}}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("tool_call", result);
        Assert.Contains("bash", result);
    }

    [Fact]
    public void FormatLine_DoneEvent_SurfacesStatus()
    {
        var d = new CodexProgressDigester();
        var json = """{"type":"done","exit_code":0}""";
        var result = d.FormatLine(Parse(json), TimeSpan.Zero);
        Assert.NotNull(result);
        Assert.Contains("done", result);
        Assert.Contains("ok", result);
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
