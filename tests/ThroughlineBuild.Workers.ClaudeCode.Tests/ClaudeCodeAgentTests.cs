using System.Diagnostics;
using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public class WorkerResultParserTests
{
    [Fact]
    public void TryParse_ValidWorkerResult_ReturnsResult()
    {
        var stdout =
            "Some preamble\n" +
            "```json\n" +
            "WORKER_RESULT\n" +
            "{\"Status\":\"Ok\",\"Summary\":\"done\",\"FilesChanged\":[\"foo.cs\"],\"FailureReason\":null,\"Metadata\":{}}\n" +
            "```\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(result);
        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("done", result.Summary);
        Assert.Equal(new[] { "foo.cs" }, result.FilesChanged);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void TryParse_NoMarker_ReturnsNull()
    {
        var stdout = "Some output\nwithout any marker\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "this is not valid json\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_EmptyStdout_ReturnsNull()
    {
        var result = WorkerResultParser.TryParse(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MarkerWithNoFollowingLine_ReturnsNull()
    {
        // Marker is the last line with nothing after it
        var stdout = "WORKER_RESULT";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MarkerWithWhitespaceOnlyFollowingLines_ReturnsNull()
    {
        var stdout = "WORKER_RESULT\n   \n   \n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MarkerWithLeadingWhitespace_ReturnsResult()
    {
        // Marker line has surrounding whitespace - should still match via Trim()
        var stdout =
            "  WORKER_RESULT  \n" +
            "{\"Status\":\"Failed\",\"Summary\":\"oops\",\"FilesChanged\":[],\"FailureReason\":\"bad\",\"Metadata\":{}}\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(result);
        Assert.Equal(Status.Failed, result.Status);
        Assert.Equal("oops", result.Summary);
        Assert.Equal("bad", result.FailureReason);
    }

    [Fact]
    public void TryParse_NeedsRework_StatusParsedCorrectly()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"Status\":\"NeedsRework\",\"Summary\":\"try again\",\"FilesChanged\":[],\"FailureReason\":\"partial\",\"Metadata\":{}}\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(result);
        Assert.Equal(Status.NeedsRework, result.Status);
    }

    [Fact]
    public void TryParse_EscalateStatus_ParsedCorrectly()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"Status\":\"Escalate\",\"Summary\":\"need help\",\"FilesChanged\":[],\"FailureReason\":\"unclear\",\"Metadata\":{}}\n";

        var result = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(result);
        Assert.Equal(Status.Escalate, result.Status);
    }
}

public class ClaudeCodeAgentNameTests
{
    [Fact]
    public void Name_Returns_ClaudeCode()
    {
        var agent = new ClaudeCodeAgent();

        Assert.Equal("claude-code", agent.Name);
    }

    [Fact]
    public void Name_Returns_ClaudeCode_WithCustomOptions()
    {
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions { ExecutablePath = "/usr/bin/claude" });

        Assert.Equal("claude-code", agent.Name);
    }
}

public class ClaudeCodeAgentConfigureEnvironmentTests
{
    [Fact]
    public void ConfigureEnvironment_RemovesAnthropicKey_WhenParentHasIt()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        psi.Environment["ANTHROPIC_API_KEY"] = "parent-key";

        ClaudeCodeAgent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromSeconds(30)));

        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void ConfigureEnvironment_ExplicitOverrideWins_WhenApiKeyInOptions()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        psi.Environment["ANTHROPIC_API_KEY"] = "parent-key";

        var options = new WorkerOptions(
            TimeSpan.FromSeconds(30),
            EnvironmentVariables: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "explicit-key" });
        ClaudeCodeAgent.ConfigureEnvironment(psi, options);

        Assert.Equal("explicit-key", psi.Environment["ANTHROPIC_API_KEY"]);
    }
}
