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
        var stdout = MakeEnvelope("some text without marker");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 1, stderr: "crash");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("1", result.FailureReason);
    }

    [Fact]
    public void EnvelopeParser_MalformedWorkerResultJson_ReturnsEscalateWithDeserializeAttribution()
    {
        var stdout = MakeEnvelope("WORKER_RESULT\nthis is not valid json");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Escalate, result.Status);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Failed to deserialize WORKER_RESULT JSON", result.FailureReason);
        Assert.Contains("JsonException", result.FailureReason);
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

// Regression lock for the AOT serialization trap documented in
// docs/throughline-build-architecture.md (section "AOT serialization traps").
//
// WorkerResultParser.TryParse must use the source-gen overload
// (ClaudeCodeJsonContext.Default.WorkerResultDto), not the reflection-based
// JsonSerializer.Deserialize<T>. Under PublishAot=true the Cli project emits
// a build.runtimeconfig.json with IsReflectionEnabledByDefault=false; any
// call through the reflection path throws NotSupportedException which the
// bare catch swallows, producing a silent null result (the TLB-108 bug).
//
// These tests prove the source-gen path is wired and functional without
// touching the process-global AppContext reflection switch (which would race
// with parallel test classes that use reflection-based helpers).
public class WorkerResultParserAotRegressionTests
{
    // Verify that ClaudeCodeJsonContext has WorkerResultDto registered and
    // that the source-gen overload deserializes the full happy-path payload.
    // This is the direct proof that the AOT path is wired: if WorkerResultDto
    // were absent from the context, .Default.WorkerResultDto would throw at
    // class-load time (not at runtime), so reaching the Assert.NotNull confirms
    // source-gen registration is present.
    [Fact]
    public void SourceGenContext_HasWorkerResultDto_AndDeserializesHappyPath()
    {
        var json =
            "{\"status\":\"Ok\",\"summary\":\"plan complete\",\"files_changed\":[\"src/Foo.cs\"]," +
            "\"failure_reason\":null,\"metadata\":{\"plan_html\":\"<p>plan</p>\"," +
            "\"risk_label\":\"low\",\"size_label\":\"M\",\"planned_at_sha\":\"abc123\"}}";

        // Direct source-gen call - this is exactly what TryParse now uses.
        // If WorkerResultDto is not registered in ClaudeCodeJsonContext,
        // this line throws InvalidOperationException at runtime.
        var dto = System.Text.Json.JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.Ok, dto.Status);
        Assert.Equal("plan complete", dto.Summary);
        Assert.NotNull(dto.FilesChanged);
        Assert.Single(dto.FilesChanged, "src/Foo.cs");
        Assert.Null(dto.FailureReason);
        Assert.NotNull(dto.Metadata);
        Assert.True(dto.Metadata.ContainsKey("plan_html"));
    }

    [Fact]
    public void SourceGenContext_WorkerResultDto_DeserializesNeedsReworkStatus()
    {
        var json =
            "{\"status\":\"NeedsRework\",\"summary\":\"try again\"," +
            "\"files_changed\":[],\"failure_reason\":\"partial\",\"metadata\":{}}";

        var dto = System.Text.Json.JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.NeedsRework, dto.Status);
        Assert.Equal("partial", dto.FailureReason);
    }

    [Fact]
    public void SourceGenContext_WorkerResultDto_DeserializesEscalateStatus()
    {
        var json =
            "{\"status\":\"Escalate\",\"summary\":\"escalated\"," +
            "\"files_changed\":[],\"failure_reason\":\"unclear\",\"metadata\":{}}";

        var dto = System.Text.Json.JsonSerializer.Deserialize(json, ClaudeCodeJsonContext.Default.WorkerResultDto);

        Assert.NotNull(dto);
        Assert.Equal(Status.Escalate, dto.Status);
    }

    // End-to-end: TryParse with metadata containing plan_html returns a
    // WorkerResult whose Metadata map carries the key. Validates that the
    // Dictionary<string, JsonElement> -> Dictionary<string, object> projection
    // in TryParse preserves keys for downstream TryGetString consumers.
    [Fact]
    public void TryParse_MetadataWithPlanHtml_KeyPresentInResult()
    {
        var stdout =
            "WORKER_RESULT\n" +
            "{\"status\":\"Ok\",\"summary\":\"done\",\"files_changed\":[],\"failure_reason\":null," +
            "\"metadata\":{\"plan_html\":\"<p>x</p>\",\"risk_label\":\"low\"," +
            "\"size_label\":\"S\",\"planned_at_sha\":\"deadbeef\"}}\n";

        var outcome = WorkerResultParser.TryParse(stdout);

        Assert.NotNull(outcome.Result);
        Assert.Equal(Status.Ok, outcome.Result.Status);
        Assert.True(outcome.Result.Metadata.ContainsKey("plan_html"));
        Assert.True(outcome.Result.Metadata.ContainsKey("risk_label"));
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
