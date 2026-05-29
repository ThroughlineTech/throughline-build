using System.Diagnostics;
using System.Text.Json;
using Xunit;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public class WorkerResultParserTests
{
    [Fact]
    public void TryParse_ValidWorkerResult_ReturnsResult()
    {
        var stdout =
            "Some preamble\n" +
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[\"foo.cs\"],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("done", outcome.Result.Summary);
        Assert.Equal(new[] { "foo.cs" }, outcome.Result.FilesChanged);
        Assert.Null(outcome.Result.FailureReason);
    }

    [Fact]
    public void TryParse_PrettyPrintedMultiLineJson_ReturnsResult()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\n" +
            "  \"status\": \"Ok\",\n" +
            "  \"summary\": \"plan complete\",\n" +
            "  \"files_changed\": [],\n" +
            "  \"failure_reason\": null,\n" +
            "  \"metadata\": {\n" +
            "    \"plan_html\": \"<p>plan</p>\",\n" +
            "    \"risk_label\": \"low\",\n" +
            "    \"size_label\": \"S\"\n" +
            "  }\n" +
            "}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("plan complete", outcome.Result.Summary);
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_html"));
    }

    [Fact]
    public void TryParse_NoMarker_ReturnsMarkerMissing()
    {
        var stdout = "Some output\nwithout any marker\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Null(outcome.DeserializeErrorType);
        Assert.Null(outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsDeserializeFailedWithAttribution()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "this is not valid json\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.DeserializeErrorType);
        Assert.Equal("JsonException", outcome.DeserializeErrorType);
        Assert.NotNull(outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_EmptyStdout_ReturnsMarkerMissing()
    {
        var outcome = WorkerResultParser.TryParse(string.Empty);

        Assert.Null(outcome.Result);
        Assert.Null(outcome.DeserializeErrorType);
    }

    [Fact]
    public void TryParse_MarkerWithNoFollowingContent_ReturnsDeserializeFailed()
    {
        var stdout = "WORKER_RESULT";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.DeserializeErrorType);
    }

    [Fact]
    public void TryParse_MarkerWithWhitespaceOnlyFollowingLines_ReturnsDeserializeFailed()
    {
        var stdout = "WORKER_RESULT\n   \n   \n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.DeserializeErrorType);
    }

    [Fact]
    public void TryParse_MarkerWithLeadingWhitespace_ReturnsResult()
    {
        var stdout =
            "  WORKER_RESULT  \n" +
            "{\"status\":\"Failed\",\"summary\":\"oops\",\"files_changed\":[],\"failure_reason\":\"bad\",\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Failed, outcome.Result.Status);
        Assert.Equal("oops", outcome.Result.Summary);
        Assert.Equal("bad", outcome.Result.FailureReason);
    }

    [Fact]
    public void TryParse_NeedsRework_StatusParsedCorrectly()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"NeedsRework\",\"summary\":\"try again\",\"files_changed\":[],\"failure_reason\":\"partial\",\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.NeedsRework, outcome.Result.Status);
    }

    [Fact]
    public void TryParse_EscalateStatus_ParsedCorrectly()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Escalate\",\"summary\":\"need help\",\"files_changed\":[],\"failure_reason\":\"unclear\",\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Escalate, outcome.Result.Status);
    }

    [Fact]
    public void TryParse_MissingStatusField_ReturnsValidationError()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("ValidationError", outcome.DeserializeErrorType);
        Assert.Contains("status", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_EmptySummary_ReturnsValidationError()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("ValidationError", outcome.DeserializeErrorType);
        Assert.Contains("summary", outcome.DeserializeErrorMessage);
    }

    [Fact]
    public void TryParse_MissingSummaryField_ReturnsValidationError()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.Equal("ValidationError", outcome.DeserializeErrorType);
        Assert.Contains("summary", outcome.DeserializeErrorMessage);
    }

    // Worker (notably Sonnet) sometimes mirrors the plan template's fenced
    // example layout and emits the WORKER_RESULT envelope inside a triple-
    // backtick code fence. Without fence-stripping the deserializer sees a
    // backtick at byte 0 and aborts with
    // "'`' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0".
    [Fact]
    public void TryParse_FencedPayload_WithJsonLanguageTag_ReturnsResult()
    {
        var stdout =
            "Some preamble\n" +
            "WORKER_RESULT\n" +
            "```json\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[\"foo.cs\"],\"failure_reason\":null,\"metadata\":{}}\n" +
            "```\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("done", outcome.Result.Summary);
        Assert.Equal(new[] { "foo.cs" }, outcome.Result.FilesChanged);
    }

    [Fact]
    public void TryParse_FencedPayload_NoLanguageTag_ReturnsResult()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "```\n" +
            "{\"status\":\"Ok\",\"summary\":\"plan complete\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{\"risk_label\":\"low\"}}\n" +
            "```\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("plan complete", outcome.Result.Summary);
    }

    [Fact]
    public void TryParse_FencedPrettyPrintedPayload_ReturnsResult()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "```json\n" +
            "{\n" +
            "  \"status\": \"Ok\",\n" +
            "  \"summary\": \"multiline\",\n" +
            "  \"files_changed\": [],\n" +
            "  \"failure_reason\": null,\n" +
            "  \"metadata\": {}\n" +
            "}\n" +
            "```";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.Equal("multiline", outcome.Result.Summary);
    }

    // Spec: envelope is the LAST output. If a worker echoes the template example
    // block first (which has placeholder text inside the fence and would fail to
    // deserialize), the parser must still pick up the real envelope that follows.
    [Fact]
    public void TryParse_TwoMarkers_FirstMalformed_SecondValid_ReturnsSecond()
    {
        var stdout =
            "Here is the envelope I will emit:\n" +
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"<placeholder>\",\"files_changed\":[],bogus}\n" +
            "Now the real result:\n" +
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"actual\",\"files_changed\":[\"a.cs\"],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal("actual", outcome.Result.Summary);
        Assert.Equal(new[] { "a.cs" }, outcome.Result.FilesChanged);
    }

    // Guards against an over-eager fence strip mangling legitimate content:
    // a backtick inside a JSON string value (e.g. in a markdown summary)
    // must survive intact.
    [Fact]
    public void TryParse_BacktickInsideStringValue_NotStripped()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"uses `foo` syntax\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal("uses `foo` syntax", outcome.Result.Summary);
    }

    // Regression-guard: when every candidate fails, the reverse-scan must still
    // surface a DeserializeFailed outcome (not silently fall through to MarkerMissing).
    [Fact]
    public void TryParse_AllCandidatesMalformed_ReturnsDeserializeFailed()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "first garbage\n" +
            "WORKER_RESULT\n" +
            "second garbage\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.DeserializeErrorType);
        Assert.Equal("JsonException", outcome.DeserializeErrorType);
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
        "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[\"foo.cs\"],\"failure_reason\":null,\"metadata\":{}}\n";

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
    public void EnvelopeParser_MalformedJson_ReturnsFailed()
    {
        var stdout = "this is not valid json at all";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Failed to parse Claude Code JSON envelope", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_MissingResultField_ReturnsFailed()
    {
        // result field is absent (null) - envelope with is_error:false but no result
        var stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":null}";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Failed, result.Status);
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
    public void EnvelopeParser_ValidEnvelope_NoWorkerResultMarker_ReturnsFailed()
    {
        // Valid envelope but inner result has no WORKER_RESULT block
        var stdout = MakeEnvelope("Hello, this is a plain response with no marker.");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("WORKER_RESULT", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_NonZeroExitCodeAfterNoMarker_ReturnsFailed()
    {
        var stdout = MakeEnvelope("some text without marker");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 1, stderr: "crash");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("1", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_MalformedWorkerResultJson_ReturnsFailedWithDeserializeAttribution()
    {
        var stdout = MakeEnvelope("WORKER_RESULT\nthis is not valid json");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Failed to deserialize WORKER_RESULT JSON", result.FailureReason);
        Assert.Contains("JsonException", result.FailureReason);
    }

    // NDJSON path: stream-json output is a sequence of events; the last type=result
    // line carries the terminal envelope. The parser must locate that line and
    // route its inner result text through WorkerResultParser exactly like the
    // legacy single-blob path.
    [Fact]
    public void EnvelopeParser_NdjsonStream_LocatesTerminalResultLine()
    {
        var systemLine = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc12345-6789-aaaa-bbbb-ccccddddeeee\",\"model\":\"claude-opus-4-6\"}";
        var assistantLine = "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-4-6\",\"content\":[{\"type\":\"text\",\"text\":\"thinking\"}]}}";
        var resultLine = MakeEnvelope(ValidWorkerResultBlock);
        var stdout = systemLine + "\n" + assistantLine + "\n" + resultLine + "\n";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("done", result.Summary);
        Assert.Equal(new[] { "foo.cs" }, result.FilesChanged);
    }

    [Fact]
    public void EnvelopeParser_NdjsonStream_MissingTerminalResult_ReturnsFailed()
    {
        // NDJSON stream that ends before the terminal result event arrives
        var systemLine = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\",\"model\":\"x\"}";
        var assistantLine = "{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}";
        var stdout = systemLine + "\n" + assistantLine + "\n";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_NdjsonStream_WithRateLimitEvents_IgnoresAndFindsResult()
    {
        // Real stream-json output interleaves rate_limit_event lines. The scanner
        // must skip them rather than failing on unrecognized type values.
        var rateLimitLine = "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\"}}";
        var resultLine = MakeEnvelope(ValidWorkerResultBlock);
        var stdout = rateLimitLine + "\n" + resultLine + "\n";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("done", result.Summary);
    }
}

public class ClaudeCodeAgentNdjsonFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Fixture_StreamHello_HasSystemAssistantResultEvents()
    {
        var path = FixturePath("stream-json-hello.ndjson");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        // Must have at least one of each kind we care about.
        Assert.Contains(lines, l => l.Contains("\"type\":\"system\""));
        Assert.Contains(lines, l => l.Contains("\"type\":\"assistant\""));
        Assert.Contains(lines, l => l.Contains("\"type\":\"result\""));
    }

    [Fact]
    public void Fixture_StreamTools_ContainsToolUseAndResult()
    {
        var path = FixturePath("stream-json-tools.ndjson");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        Assert.Contains(lines, l => l.Contains("\"type\":\"tool_use\"") || l.Contains("\"tool_use\""));
        Assert.Contains(lines, l => l.Contains("\"type\":\"result\""));
    }

    // Feeding the raw NDJSON fixture through ParseStdoutEnvelope should succeed
    // for the hello fixture (terminal result has a plain text answer; no
    // WORKER_RESULT marker => Failed with "No WORKER_RESULT" message).
    [Fact]
    public void Fixture_StreamHello_ParsesEnvelopeWithoutCrash()
    {
        var path = FixturePath("stream-json-hello.ndjson");
        var stdout = File.ReadAllText(path);

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        // Real fixture has no WORKER_RESULT block in the result text; we expect
        // Failed with the "No WORKER_RESULT" message. This proves the envelope
        // located the terminal result line and routed it to the WORKER_RESULT
        // parser exactly like the legacy single-blob path.
        Assert.Equal(Status.Failed, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("WORKER_RESULT", result.FailureReason);
    }
}

public class ClaudeCodeAgentConfigureEnvironmentTests
{
    [Fact]
    public void ConfigureEnvironment_RemovesAnthropicKey_WhenParentHasIt()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        psi.Environment["ANTHROPIC_API_KEY"] = "parent-key";

        new ClaudeCodeAgent().ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromSeconds(30)));

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
        new ClaudeCodeAgent().ConfigureEnvironment(psi, options);

        Assert.Equal("explicit-key", psi.Environment["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void ConfigureEnvironment_MaxOutputTokensSet_SetsEnvVar()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions { MaxOutputTokens = 32000 });

        agent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromSeconds(30)));

        Assert.Equal("32000", psi.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
    }

    [Fact]
    public void ConfigureEnvironment_MaxOutputTokensNull_LeavesEnvUnchanged()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions { MaxOutputTokens = null });

        agent.ConfigureEnvironment(psi, new WorkerOptions(TimeSpan.FromSeconds(30)));

        Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_MAX_OUTPUT_TOKENS"));
    }

    [Fact]
    public void ConfigureEnvironment_UserOverrideWins_WhenBothPresent()
    {
        var psi = new ProcessStartInfo("echo") { UseShellExecute = false };
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions { MaxOutputTokens = 32000 });
        var options = new WorkerOptions(
            TimeSpan.FromSeconds(30),
            EnvironmentVariables: new Dictionary<string, string> { ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "16384" });

        agent.ConfigureEnvironment(psi, options);

        Assert.Equal("16384", psi.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]);
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
        Type: "result", Subtype: "success", IsError: false, Result: result, Usage: null, TotalCostUsd: null);

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

public class ClaudeCodeAgentLiveStreamTests
{
    [Fact]
    public void WriteWorkerLine_NullSink_NoOp()
    {
        // Calling with a null sink must not throw and must not write to the sentinel.
        var sentinel = new System.IO.StringWriter();

        // Call with null sink - sentinel must remain empty
        ClaudeCodeAgent.WriteWorkerLine(null, "worker> ", "some line");

        Assert.Equal(string.Empty, sentinel.ToString());
    }

    [Fact]
    public void WriteWorkerLine_NonNullSink_WritesPrefixedLine()
    {
        var sink = new System.IO.StringWriter();

        ClaudeCodeAgent.WriteWorkerLine(sink, "worker> ", "hello world");

        var written = sink.ToString();
        Assert.Contains("worker> hello world", written);
    }

    [Fact]
    public void WriteWorkerLine_RespectsPrefix_StdoutVsStderr()
    {
        var stdoutSink = new System.IO.StringWriter();
        var stderrSink = new System.IO.StringWriter();

        ClaudeCodeAgent.WriteWorkerLine(stdoutSink, "worker> ", "stdout line");
        ClaudeCodeAgent.WriteWorkerLine(stderrSink, "worker! ", "stderr line");

        Assert.Contains("worker> stdout line", stdoutSink.ToString());
        Assert.Contains("worker! stderr line", stderrSink.ToString());
        // Cross-check: stdout sink has no "!" prefix, stderr sink has no ">" prefix
        Assert.DoesNotContain("worker! ", stdoutSink.ToString());
        Assert.DoesNotContain("worker> ", stderrSink.ToString());
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
            Usage: usage,
            TotalCostUsd: null
        );
        var wallClockMs = 1234L;

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, wallClockMs);

        Assert.Equal(7, metadata.Count);
        Assert.Null(metadata["model"]);
        Assert.Equal("anthropic", metadata["vendor"]);
        Assert.Equal(10, metadata["input_tokens"]);
        Assert.Equal(20, metadata["output_tokens"]);
        Assert.Equal(5, metadata["cache_read_tokens"]);
        Assert.Equal(3, metadata["cache_create_tokens"]);
        Assert.Equal(1234L, metadata["wall_clock_ms"]);
        Assert.False(metadata.ContainsKey("partial"));
        Assert.False(metadata.ContainsKey("cost_usd"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_UsageAbsent_ReturnsZerosWithPartialFlag()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: null,
            TotalCostUsd: null
        );
        var wallClockMs = 5678L;

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, wallClockMs);

        Assert.Equal(8, metadata.Count);
        Assert.Null(metadata["model"]);
        Assert.Equal("anthropic", metadata["vendor"]);
        Assert.Equal(0, metadata["input_tokens"]);
        Assert.Equal(0, metadata["output_tokens"]);
        Assert.Null(metadata["cache_read_tokens"]);
        Assert.Null(metadata["cache_create_tokens"]);
        Assert.Equal(5678L, metadata["wall_clock_ms"]);
        Assert.True((bool)metadata["partial"]);
        Assert.False(metadata.ContainsKey("cost_usd"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithModel_PopulatesModelAndVendor()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: new ClaudeCodeUsage(InputTokens: 1, OutputTokens: 1, CacheReadInputTokens: null, CacheCreationInputTokens: null),
            TotalCostUsd: null
        );

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, 0, "claude-opus-4-6");

        Assert.Equal("claude-opus-4-6", metadata["model"]);
        Assert.Equal("anthropic", metadata["vendor"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithCost_PopulatesCostUsd()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: new ClaudeCodeUsage(InputTokens: 1, OutputTokens: 1, CacheReadInputTokens: null, CacheCreationInputTokens: null),
            TotalCostUsd: 0.0123m
        );

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, 0);

        Assert.True(metadata.ContainsKey("cost_usd"));
        Assert.Equal((double)0.0123m, metadata["cost_usd"]);
    }

    [Fact]
    public void BuildLlmUsageMetadata_WithoutCost_OmitsCostUsd()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: new ClaudeCodeUsage(InputTokens: 1, OutputTokens: 1, CacheReadInputTokens: null, CacheCreationInputTokens: null),
            TotalCostUsd: null
        );

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, 0);

        Assert.False(metadata.ContainsKey("cost_usd"));
    }

    [Fact]
    public void BuildLlmUsageMetadata_VendorParameter_OverridesDefault()
    {
        var envelope = new ClaudeCodeJsonEnvelope(
            Type: "result",
            Subtype: "success",
            IsError: false,
            Result: "some result",
            Usage: new ClaudeCodeUsage(InputTokens: 1, OutputTokens: 1, CacheReadInputTokens: null, CacheCreationInputTokens: null),
            TotalCostUsd: null
        );

        var metadata = ClaudeCodeAgent.BuildLlmUsageMetadata(envelope, 0, vendor: "test-vendor");

        Assert.Equal("test-vendor", metadata["vendor"]);
    }
}

public class ClaudeCodeAgentModelExtractionTests
{
    [Fact]
    public void TryExtractModelFromStream_SystemEventPresent_ReturnsModel()
    {
        var systemLine = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\",\"model\":\"claude-opus-4-6\"}";
        var resultLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
        var stdout = systemLine + "\n" + resultLine + "\n";

        var model = ClaudeCodeAgent.TryExtractModelFromStream(stdout);

        Assert.Equal("claude-opus-4-6", model);
    }

    [Fact]
    public void TryExtractModelFromStream_NoSystemEvent_ReturnsNull()
    {
        var resultLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

        var model = ClaudeCodeAgent.TryExtractModelFromStream(resultLine + "\n");

        Assert.Null(model);
    }

    [Fact]
    public void TryExtractModelFromStream_EmptyStdout_ReturnsNull()
    {
        var model = ClaudeCodeAgent.TryExtractModelFromStream(string.Empty);

        Assert.Null(model);
    }

    [Fact]
    public void TryExtractModelFromStream_HelloFixture_ExtractsModel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stream-json-hello.ndjson");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        var stdout = File.ReadAllText(path);

        var model = ClaudeCodeAgent.TryExtractModelFromStream(stdout);

        Assert.Equal("claude-opus-4-6", model);
    }

    [Fact]
    public void ParseStdoutEnvelope_WithSystemEvent_PopulatesModelInLlmUsage()
    {
        var systemLine = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc\",\"model\":\"claude-sonnet-4-6\"}";
        var workerResult =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";
        var resultLine = $"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":{System.Text.Json.JsonSerializer.Serialize(workerResult)},\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}";
        var stdout = systemLine + "\n" + resultLine + "\n";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Metadata.ContainsKey("llm_usage"));
        var llmUsage = (System.Collections.Generic.Dictionary<string, object>)result.Metadata["llm_usage"];
        Assert.Equal("claude-sonnet-4-6", llmUsage["model"]);
        Assert.Equal("anthropic", llmUsage["vendor"]);
    }

    [Fact]
    public void ParseStdoutEnvelope_FallbackModel_UsedWhenNoSystemEvent()
    {
        var workerResult =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null,\"metadata\":{}}\n";
        var resultLine = $"{{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":{System.Text.Json.JsonSerializer.Serialize(workerResult)},\"usage\":{{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}}";

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(resultLine + "\n", exitCode: 0, stderr: "", fallbackModel: "claude-haiku-3-5");

        Assert.Equal(Status.Ok, result.Status);
        var llmUsage = (System.Collections.Generic.Dictionary<string, object>)result.Metadata["llm_usage"];
        Assert.Equal("claude-haiku-3-5", llmUsage["model"]);
        Assert.Equal("anthropic", llmUsage["vendor"]);
    }
}

public class ClaudeCodeProgressDigesterTests
{
    // Tests use TimeSpan-based offsets so they are deterministic regardless of
    // wall-clock skew. The DateTimeOffset overload is exercised indirectly via
    // ClaudeCodeAgentDigestRoutingTests below.
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // Clone to detach from the disposed JsonDocument lifetime.
        return doc.RootElement.Clone();
    }

    [Fact]
    public void FormatLine_SystemEvent_EmitsInitLineWithSessionAndModel()
    {
        var el = Parse("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"abc12345-6789-aaaa-bbbb-ccccddddeeee\",\"model\":\"claude-opus-4-6\"}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(3));

        Assert.NotNull(line);
        Assert.StartsWith("[0:03] ", line);
        Assert.Contains("system", line);
        Assert.Contains("abc12345", line);
        Assert.Contains("claude-opus-4-6", line);
    }

    [Fact]
    public void FormatLine_AssistantToolUseRead_EmitsToolUseLineWithFilePath()
    {
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"docs/foo.md\"}}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(15)));

        Assert.NotNull(line);
        Assert.StartsWith("[1:15] ", line);
        Assert.Contains("tool_use", line);
        Assert.Contains("Read", line);
        Assert.Contains("docs/foo.md", line);
    }

    [Fact]
    public void FormatLine_AssistantToolUseGrep_SurfacesPatternArg()
    {
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Grep\",\"input\":{\"pattern\":\"plan-enriched\"}}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(8));

        Assert.NotNull(line);
        Assert.StartsWith("[0:08] ", line);
        Assert.Contains("Grep", line);
        Assert.Contains("plan-enriched", line);
    }

    [Fact]
    public void FormatLine_AssistantToolUseBash_SurfacesCommandArg()
    {
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"git status\"}}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromMinutes(2));

        Assert.NotNull(line);
        Assert.StartsWith("[2:00] ", line);
        Assert.Contains("Bash", line);
        Assert.Contains("git status", line);
    }

    [Fact]
    public void FormatLine_AssistantTextOnly_EmitsTurnMarker()
    {
        // Text/thinking-only assistant turn coalesces to a single "turn" line
        // (no per-block deltas at default verbosity).
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"text\",\"text\":\"some response text\"}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(47));

        Assert.NotNull(line);
        Assert.StartsWith("[0:47] ", line);
        Assert.Contains("assistant", line);
        Assert.Contains("turn", line);
    }

    [Fact]
    public void FormatLine_AssistantThinkingOnly_EmitsTurnMarker()
    {
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"reasoning...\"}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        Assert.Contains("assistant", line);
        Assert.Contains("turn", line);
    }

    [Fact]
    public void FormatLine_TerminalResult_EmitsResultLineWithTokens()
    {
        var el = Parse("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"input_tokens\":3,\"output_tokens\":23888,\"cache_read_input_tokens\":317000}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, new TimeSpan(0, 7, 30));

        Assert.NotNull(line);
        Assert.StartsWith("[7:30] ", line);
        Assert.Contains("result", line);
        Assert.Contains("ok", line);
        Assert.Contains("23888", line);
        Assert.Contains("317000", line);
    }

    [Fact]
    public void FormatLine_TerminalResultIsError_EmitsErrMarker()
    {
        var el = Parse("{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"usage\":{\"output_tokens\":0,\"cache_read_input_tokens\":0}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(12));

        Assert.NotNull(line);
        Assert.Contains("err", line);
    }

    [Fact]
    public void FormatLine_RateLimitEvent_ReturnsNull()
    {
        // Unknown / decoration event must not produce a digest line.
        var el = Parse("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\"}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(1));

        Assert.Null(line);
    }

    [Fact]
    public void FormatLine_UserEvent_ReturnsNull()
    {
        // The user event (tool_result echo) is not surfaced today.
        var el = Parse("{\"type\":\"user\",\"message\":{\"role\":\"user\"}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(1));

        Assert.Null(line);
    }

    [Fact]
    public void FormatLine_TruncatesPayloadOverMaxChars()
    {
        // Build a file_path longer than MaxPayloadChars (no slashes, so LastSegments
        // leaves it unchanged) and verify the digest line truncates with an ellipsis.
        var longPath = new string('x', ClaudeCodeProgressDigester.MaxPayloadChars + 20);
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"" + longPath + "\"}}]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(3));

        Assert.NotNull(line);
        Assert.Contains("...", line);
        // The bare "x" run after the prefix must be at most 80 chars.
        // We assert the rendered line is no longer than the prefix + max payload.
        var maxLine = "[0:03] ".Length + 10 + 1 + ClaudeCodeProgressDigester.MaxPayloadChars;
        Assert.True(line.Length <= maxLine,
            $"line too long: {line.Length} > {maxLine}: '{line}'");
    }

    [Fact]
    public void FormatLine_MultipleToolUsesInSingleTurn_EmitsLinePerToolUse()
    {
        // A single assistant message can include multiple tool_use blocks.
        // Each must produce its own digest line.
        var el = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[" +
            "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}," +
            "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"b.cs\"}}" +
            "]}}");

        var line = new ClaudeCodeProgressDigester().FormatLine(el, TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        var subLines = line.Split('\n');
        Assert.Equal(2, subLines.Length);
        Assert.Contains("a.cs", subLines[0]);
        Assert.Contains("b.cs", subLines[1]);
    }

    [Theory]
    [InlineData(0, 3, "0:03")]
    [InlineData(1, 15, "1:15")]
    [InlineData(7, 30, "7:30")]
    [InlineData(12, 34, "12:34")]
    public void FormatOffset_FormatsMinutesAndSeconds(int minutes, int seconds, string expected)
    {
        var ts = new TimeSpan(0, minutes, seconds);

        var actual = ClaudeCodeProgressDigester.FormatOffset(ts);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatOffset_HourBoundary_FormatsAsHhMmSs()
    {
        var ts = new TimeSpan(1, 5, 30);

        var actual = ClaudeCodeProgressDigester.FormatOffset(ts);

        Assert.Equal("1:05:30", actual);
    }

    [Fact]
    public void FormatLine_MalformedJson_ReturnsNull()
    {
        // The public FormatLine(string) must not throw on malformed input.
        var digester = new ClaudeCodeProgressDigester();
        var result = digester.FormatLine("not valid json {{{");
        Assert.Null(result);
    }

    [Fact]
    public void ClaudeCodeAgent_Digester_ReturnsNonNullClaudeCodeProgressDigesterInstance()
    {
        var agent = new ClaudeCodeAgent();
        var digester = agent.Digester;
        Assert.NotNull(digester);
        Assert.IsType<ClaudeCodeProgressDigester>(digester);
    }

    [Fact]
    public void NullDigester_ProgressDigestSink_ReceivesNoLines()
    {
        // Confirm that when IWorkerProgressDigester is null, no digest lines are written
        // and no exception is thrown. We simulate the phase-level call pattern.
        IWorkerProgressDigester? digester = null;
        var sink = new System.IO.StringWriter();
        var rawLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"output_tokens\":5,\"cache_read_input_tokens\":0}}";

        // This is the exact call pattern that phases and the OutputDataReceived handler use.
        var formatted = digester?.FormatLine(rawLine);
        if (formatted != null) sink.WriteLine(formatted);

        Assert.Equal(string.Empty, sink.ToString());
    }
}

public class ClaudeCodeAgentDigestRoutingTests
{
    [Fact]
    public void TryEmitDigestLine_NdjsonResultEvent_WritesFormattedLineToSink()
    {
        var sink = new System.IO.StringWriter();
        var resultLine = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":{\"output_tokens\":42,\"cache_read_input_tokens\":100}}";

        ClaudeCodeAgent.TryEmitDigestLine(resultLine, sink, DateTimeOffset.UtcNow);

        var written = sink.ToString();
        Assert.Contains("result", written);
        Assert.Contains("42", written);
    }

    [Fact]
    public void TryEmitDigestLine_MalformedJson_DoesNotThrowAndDoesNotWrite()
    {
        var sink = new System.IO.StringWriter();

        // Must not throw - digest is best-effort.
        var exception = Record.Exception(() =>
            ClaudeCodeAgent.TryEmitDigestLine("not json at all", sink, DateTimeOffset.UtcNow));

        Assert.Null(exception);
        Assert.Equal(string.Empty, sink.ToString());
    }

    [Fact]
    public void TryEmitDigestLine_FilteredEvent_DoesNotWrite()
    {
        var sink = new System.IO.StringWriter();
        var rateLimitLine = "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\"}}";

        ClaudeCodeAgent.TryEmitDigestLine(rateLimitLine, sink, DateTimeOffset.UtcNow);

        Assert.Equal(string.Empty, sink.ToString());
    }

    [Fact]
    public void TryEmitDigestLine_AssistantToolUseLine_WritesFormattedLine()
    {
        var sink = new System.IO.StringWriter();
        var line = "{\"type\":\"assistant\",\"message\":{\"model\":\"x\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Grep\",\"input\":{\"pattern\":\"foo\"}}]}}";

        ClaudeCodeAgent.TryEmitDigestLine(line, sink, DateTimeOffset.UtcNow);

        var written = sink.ToString();
        Assert.Contains("tool_use", written);
        Assert.Contains("Grep", written);
        Assert.Contains("foo", written);
    }

    // Integration-style: feed the captured stream-json-tools.ndjson fixture
    // line-by-line through TryEmitDigestLine and confirm we get at least one
    // tool_use digest line and a final result line.
    [Fact]
    public void TryEmitDigestLine_ToolsFixture_ProducesToolUseAndResultLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stream-json-tools.ndjson");
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        var sink = new System.IO.StringWriter();
        var start = DateTimeOffset.UtcNow;

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ClaudeCodeAgent.TryEmitDigestLine(line, sink, start);
        }

        var written = sink.ToString();
        Assert.Contains("tool_use", written);
        Assert.Contains("Read", written);
        Assert.Contains("result", written);
    }
}

public class ClaudeCodeAgentSizeMapTests
{
    [Fact]
    public void NormalizeModel_VendorPrefixStripped_ReturnsBareName()
    {
        // NormalizeModel is the same helper used when resolving from Sizes.
        // Confirms vendor prefix is stripped so --model receives bare id.
        var result = ClaudeCodeAgent.NormalizeModel("anthropic:claude-sonnet-4-6");

        Assert.Equal("claude-sonnet-4-6", result);
    }

    [Fact]
    public void NormalizeModel_NoPrefixModel_ReturnedAsIs()
    {
        var result = ClaudeCodeAgent.NormalizeModel("claude-haiku-4-5-20251001");

        Assert.Equal("claude-haiku-4-5-20251001", result);
    }

    [Fact]
    public void NormalizeModel_NullInput_ReturnsNull()
    {
        var result = ClaudeCodeAgent.NormalizeModel(null);

        Assert.Null(result);
    }

    [Fact]
    public void NormalizeModel_EmptyInput_ReturnsNull()
    {
        var result = ClaudeCodeAgent.NormalizeModel("");

        Assert.Null(result);
    }

    [Fact]
    public void Size_Small_ResolvesToHaikuModel_ViaNormalizeModel()
    {
        // Simulates the resolution path: Sizes[Small] -> NormalizeModel -> --model value.
        var sizes = new Dictionary<WorkerSize, string>
        {
            [WorkerSize.Small] = "claude-haiku-4-5-20251001",
            [WorkerSize.Medium] = "anthropic:claude-sonnet-4-6",
            [WorkerSize.Large] = "claude-opus-4-7"
        };

        sizes.TryGetValue(WorkerSize.Small, out var rawModel);
        var modelArg = ClaudeCodeAgent.NormalizeModel(rawModel);

        Assert.Equal("claude-haiku-4-5-20251001", modelArg);
    }

    [Fact]
    public void Size_Medium_WithVendorPrefix_StripsPrefix()
    {
        var sizes = new Dictionary<WorkerSize, string>
        {
            [WorkerSize.Small] = "claude-haiku-4-5-20251001",
            [WorkerSize.Medium] = "anthropic:claude-sonnet-4-6",
            [WorkerSize.Large] = "claude-opus-4-7"
        };

        sizes.TryGetValue(WorkerSize.Medium, out var rawModel);
        var modelArg = ClaudeCodeAgent.NormalizeModel(rawModel);

        Assert.Equal("claude-sonnet-4-6", modelArg);
    }

    [Fact]
    public void EmptySizesDict_NullResolvedModel_NormalizeModelReturnsNull()
    {
        // When Sizes is empty, TryGetValue returns false and resolvedModelRaw is null.
        // NormalizeModel(null) must return null so no --model flag is appended.
        var sizes = new Dictionary<WorkerSize, string>();
        sizes.TryGetValue(WorkerSize.Medium, out var resolvedModelRaw);
        var modelArg = ClaudeCodeAgent.NormalizeModel(resolvedModelRaw);

        Assert.Null(modelArg);
    }
}

public class WriteCancellationCaptureTests
{
    [Fact]
    public void WriteCancellationCapture_WithValidCaptureDirAndContent_WritesAllFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb144-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string briefText = "This is the brief instruction";
            const string stdoutText = "partial stdout output";
            const string stderrText = "partial stderr output";

            ClaudeCodeAgent.WriteCancellationCapture(dir, briefText, stdoutText, stderrText);

            Assert.True(File.Exists(Path.Combine(dir, "worker-stdin.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "worker-stdout.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "worker-stderr.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "cancel-reason.txt")));

            Assert.Equal(briefText, File.ReadAllText(Path.Combine(dir, "worker-stdin.txt"), System.Text.Encoding.UTF8));
            Assert.Equal(stdoutText, File.ReadAllText(Path.Combine(dir, "worker-stdout.txt"), System.Text.Encoding.UTF8));
            Assert.Equal(stderrText, File.ReadAllText(Path.Combine(dir, "worker-stderr.txt"), System.Text.Encoding.UTF8));
            Assert.Contains("cancelled or timed out", File.ReadAllText(Path.Combine(dir, "cancel-reason.txt"), System.Text.Encoding.UTF8), System.StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteCancellationCapture_CaptureDirectoryNull_NoopsWithoutThrow()
    {
        // Should not throw when captureDir is null
        ClaudeCodeAgent.WriteCancellationCapture(null, "brief", "stdout", "stderr");
        // Pass - no exception
    }

    [Fact]
    public void WriteCancellationCapture_EmptyContent_WritesEmptyFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb144-cancel-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            ClaudeCodeAgent.WriteCancellationCapture(dir, "", "", "");

            Assert.Equal("", File.ReadAllText(Path.Combine(dir, "worker-stdin.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("", File.ReadAllText(Path.Combine(dir, "worker-stdout.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("", File.ReadAllText(Path.Combine(dir, "worker-stderr.txt"), System.Text.Encoding.UTF8));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteCancellationCapture_IdempotentCall_OverwritesWithoutThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb144-cancel-idem-" + Guid.NewGuid().ToString("N"));
        try
        {
            // First call
            ClaudeCodeAgent.WriteCancellationCapture(dir, "brief1", "stdout1", "stderr1");

            // Second call should overwrite idempotently
            ClaudeCodeAgent.WriteCancellationCapture(dir, "brief2", "stdout2", "stderr2");

            Assert.Equal("brief2", File.ReadAllText(Path.Combine(dir, "worker-stdin.txt"), System.Text.Encoding.UTF8));
            Assert.Equal("stdout2", File.ReadAllText(Path.Combine(dir, "worker-stdout.txt"), System.Text.Encoding.UTF8));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}

public class ClaudeCodeAgentBypassPermissionsTests
{
    [Fact]
    public void BuildArgs_BypassPermissionsTrue_IncludesDangerouslySkipPermissions()
    {
        var options = new ClaudeCodeOptions { BypassPermissions = true };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = ClaudeCodeAgent.BuildArgs(options, workerOptions);

        Assert.Contains("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void BuildArgs_BypassPermissionsFalse_OmitsDangerouslySkipPermissions()
    {
        var options = new ClaudeCodeOptions { BypassPermissions = false };
        var workerOptions = new WorkerOptions(TimeSpan.FromSeconds(30));

        var args = ClaudeCodeAgent.BuildArgs(options, workerOptions);

        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }
}
