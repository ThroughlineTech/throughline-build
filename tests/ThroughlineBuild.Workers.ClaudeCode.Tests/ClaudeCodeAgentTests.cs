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

public class ClaudeCodeAgentEnvelopeParserTests
{
    // A minimal valid envelope JSON whose result contains a WORKER_RESULT block.
    private static string MakeEnvelope(string result) =>
        $"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":{System.Text.Json.JsonSerializer.Serialize(result)},\"usage\":{{\"input_tokens\":3,\"output_tokens\":5,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}";

    private const string ValidWorkerResultBlock =
        "WORKER_RESULT\n" +
        "{\"Status\":\"Ok\",\"Summary\":\"done\",\"FilesChanged\":[\"foo.cs\"],\"FailureReason\":null,\"Metadata\":{}}\n";

    [Fact]
    public void EnvelopeParser_ValidJson_RoutesResultToWorkerResultParser()
    {
        var stdout = MakeEnvelope(ValidWorkerResultBlock);

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.NotNull(result);
        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("done", result.Summary);
        Assert.Equal(new[] { "foo.cs" }, result.FilesChanged);
    }

    [Fact]
    public void EnvelopeParser_MalformedJson_ReturnsEscalate()
    {
        var stdout = "this is not valid json at all";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Escalate, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Failed to parse Claude Code JSON envelope", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_MissingResultField_ReturnsEscalate()
    {
        // result field is absent (null) - envelope with is_error:false but no result
        var stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":null}";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Escalate, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("result field", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_IsErrorTrue_ReturnsEscalate()
    {
        var stdout = "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":null}";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 1, stderr: "some error");

        Assert.Equal(Status.Escalate, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("is_error=true", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_ValidEnvelope_NoWorkerResultMarker_ReturnsEscalate()
    {
        // Valid envelope but inner result has no WORKER_RESULT block
        var stdout = MakeEnvelope("Hello, this is a plain response with no marker.");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Escalate, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("WORKER_RESULT", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_NonZeroExitCodeAfterNoMarker_ReturnsFailed()
    {
        // Valid envelope parse, no WORKER_RESULT, but exit code is non-zero -> Failed
        var stdout = MakeEnvelope("some text without marker");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 1, stderr: "crash");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("1", result.FailureReason);
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

public class ClaudeCodeAgentDebugCaptureTests
{
    private static WorkerResult MakeOkResult(string? failureReason = null) => new WorkerResult(
        Status.Ok, "done", new[] { "foo.cs" }, failureReason,
        new Dictionary<string, object>());

    private static WorkerResult MakeEscalateResult(string failureReason) => new WorkerResult(
        Status.Escalate, "No WORKER_RESULT found in output", Array.Empty<string>(), failureReason,
        new Dictionary<string, object>());

    private static ClaudeCodeJsonEnvelope MakeEnvelope(string? result) => new ClaudeCodeJsonEnvelope(
        Type: "result", Subtype: "success", IsError: false, Result: result, Usage: null);

    [Fact]
    public void WriteDebugCapture_HappyPath_WritesFiveFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb105-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var envelope = MakeEnvelope("inner result text");
            var result = MakeOkResult();

            ClaudeCodeAgent.WriteDebugCapture(dir, "brief content", "stdout content", "stderr content", envelope, result);

            Assert.True(File.Exists(Path.Combine(dir, "worker-stdin.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "worker-stdout.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "worker-stderr.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "envelope-result.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "worker-result.json")));
            Assert.False(File.Exists(Path.Combine(dir, "parse-error.txt")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteDebugCapture_HappyPath_FileContentsMatchInputs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb105-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var envelope = MakeEnvelope("inner result text");
            var result = MakeOkResult();

            ClaudeCodeAgent.WriteDebugCapture(dir, "the brief", "the stdout", "the stderr", envelope, result);

            Assert.Equal("the brief", File.ReadAllText(Path.Combine(dir, "worker-stdin.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("the stdout", File.ReadAllText(Path.Combine(dir, "worker-stdout.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("the stderr", File.ReadAllText(Path.Combine(dir, "worker-stderr.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("inner result text", File.ReadAllText(Path.Combine(dir, "envelope-result.txt"), System.Text.Encoding.UTF8));

            var resultJson = File.ReadAllText(Path.Combine(dir, "worker-result.json"), System.Text.Encoding.UTF8);
            Assert.Contains("Ok", resultJson);
            Assert.Contains("done", resultJson);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteDebugCapture_EnvelopeNull_WritesParseErrorInsteadOfEnvelopeResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb105-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = MakeEscalateResult("Envelope result did not contain a WORKER_RESULT block. Stderr: ");

            ClaudeCodeAgent.WriteDebugCapture(dir, "brief", "stdout", "stderr", null, result);

            Assert.True(File.Exists(Path.Combine(dir, "parse-error.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "envelope-result.txt")));

            var parseError = File.ReadAllText(Path.Combine(dir, "parse-error.txt"), System.Text.Encoding.UTF8);
            Assert.Contains("WORKER_RESULT", parseError);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteDebugCapture_EnvelopeResultFieldNull_WritesParseError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb105-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var envelope = MakeEnvelope(null);  // result field is null
            var result = MakeEscalateResult("Envelope result field is null. Subtype: success. Stderr: ");

            ClaudeCodeAgent.WriteDebugCapture(dir, "brief", "stdout", "stderr", envelope, result);

            Assert.True(File.Exists(Path.Combine(dir, "parse-error.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "envelope-result.txt")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteDebugCapture_IdempotentRerun_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb105-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var envelope = MakeEnvelope("result");
            var result = MakeOkResult();

            // First call creates directory and files
            ClaudeCodeAgent.WriteDebugCapture(dir, "brief", "stdout", "stderr", envelope, result);
            // Second call must overwrite without throwing
            ClaudeCodeAgent.WriteDebugCapture(dir, "brief2", "stdout2", "stderr2", envelope, result);

            Assert.Equal("brief2", File.ReadAllText(Path.Combine(dir, "worker-stdin.txt"), System.Text.Encoding.UTF8));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}

public class ClaudeCodeAgentLlmUsageTests
{
    [Fact]
    public void BuildLlmUsageMetadata_UsagePresent_ReturnsPopulatedPayload()
    {
        var usage = new ClaudeCodeUsage(
            InputTokens: 10,
            OutputTokens: 20,
            CacheReadInputTokens: 5,
            CacheCreationInputTokens: 3
        );
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: usage
        );
        var wallClockMs = 1234L;

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, wallClockMs);

        Assert.Equal(6, metadata.Count);
        Assert.Null(metadata["model"]);
        Assert.Equal(10, metadata["input_tokens"]);
        Assert.Equal(20, metadata["output_tokens"]);
        Assert.Equal(5, metadata["cache_read_tokens"]);
        Assert.Equal(3, metadata["cache_create_tokens"]);
        Assert.Equal(1234L, metadata["wall_clock_ms"]);
        Assert.False(metadata.ContainsKey("partial"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_UsageAbsent_ReturnsZerosWithPartialFlag()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: null
        );
        var wallClockMs = 5678L;

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, wallClockMs);

        Assert.Equal(7, metadata.Count);
        Assert.Null(metadata["model"]);
        Assert.Equal(0, metadata["input_tokens"]);
        Assert.Equal(0, metadata["output_tokens"]);
        Assert.Null(metadata["cache_read_tokens"]);
        Assert.Null(metadata["cache_create_tokens"]);
        Assert.Equal(5678L, metadata["wall_clock_ms"]);
        Assert.True((bool)metadata["partial"]);
    }
}
