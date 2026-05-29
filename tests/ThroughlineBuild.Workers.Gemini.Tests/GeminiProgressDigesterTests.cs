using System.Text.Json;
using Xunit;
using ThroughlineBuild.Workers.Gemini;

namespace ThroughlineBuild.Workers.Gemini.Tests;

public class GeminiProgressDigesterTests
{
    [Fact]
    public void FormatLine_KnownEventType_Response_ProducesNonNullLine()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"response","content":"Hello world"}""";
        var result = digester.FormatLine(json);

        Assert.NotNull(result);
        Assert.Contains("response", result);
        Assert.Contains("Hello world", result);
    }

    [Fact]
    public void FormatLine_KnownEventType_ToolCall_ProducesNonNullLine()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"tool_call","name":"read_file","arguments":{"path":"/tmp/test.txt"}}""";
        var result = digester.FormatLine(json);

        Assert.NotNull(result);
        Assert.Contains("tool_call", result);
        Assert.Contains("read_file", result);
    }

    [Fact]
    public void FormatLine_KnownEventType_ToolResponse_ProducesNonNullLine()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"tool_response","name":"read_file","content":"file contents"}""";
        var result = digester.FormatLine(json);

        Assert.NotNull(result);
        Assert.Contains("tool_response", result);
        Assert.Contains("read_file", result);
    }

    [Fact]
    public void FormatLine_UnknownEventType_ReturnsNull()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"unknown_event","data":"something"}""";
        var result = digester.FormatLine(json);

        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_MalformedJson_ReturnsNull()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"response"invalid}""";
        var result = digester.FormatLine(json);

        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_EmptyString_ReturnsNull()
    {
        var digester = new GeminiProgressDigester();
        var result = digester.FormatLine("");

        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_Response_EmptyContent_ReturnsNull()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"response","content":""}""";
        var result = digester.FormatLine(json);

        Assert.Null(result);
    }

    [Fact]
    public void FormatLine_WithPinnedOffset_FormatsCorrectly()
    {
        var digester = new GeminiProgressDigester();
        var json = """{"type":"response","content":"test"}""";
        using var doc = JsonDocument.Parse(json);
        var offset = TimeSpan.FromSeconds(65); // 1m:05s
        var result = digester.FormatLine(doc.RootElement, offset);

        Assert.NotNull(result);
        Assert.Contains("[1:05]", result);
        Assert.Contains("response", result);
    }
}
