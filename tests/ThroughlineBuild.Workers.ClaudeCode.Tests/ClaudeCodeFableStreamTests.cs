using Xunit;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

// Regression coverage for Fable-style stream shapes. Claude Code's terminal result
// envelope carries ONLY the final assistant message text in its `result` field; Fable
// splits output across messages far more often than Opus/Sonnet, so parsing `result`
// alone nondeterministically loses fenced blocks (observed: scaffold profile derivation
// returning "worker did not emit a <<<PROJECT_PROFILE_START fenced block" while the
// envelope parsed clean). The agent now reconstructs the full assistant transcript from
// the NDJSON stream and parses that, falling back to `result` for the legacy single-blob
// shape. These tests pin every observed shape.
public class ClaudeCodeFableStreamTests
{
    private static string FixturePath(string name) => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", name);

    private static string ReadFixture(string name)
    {
        var path = FixturePath(name);
        Assert.True(File.Exists(path), $"Fixture missing: {path}");
        return File.ReadAllText(path);
    }

    // The sample-run-14 failure shape: fenced block in one assistant message, WORKER_RESULT
    // envelope in a later one. The result field carries only the envelope; the block
    // must still be captured from the transcript.
    [Fact]
    public void ParseStdoutEnvelope_SplitBlockAndEnvelope_BlockCaptured()
    {
        var stdout = ReadFixture("stream-json-fable-split-block.ndjson");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.NotNull(result.Blocks);
        Assert.True(result.Blocks.ContainsKey("PROJECT_PROFILE"),
            "fenced block emitted in an earlier assistant message must survive the parse");
        Assert.Contains("react-vite", result.Blocks["PROJECT_PROFILE"]);
    }

    // The mirror shape: block + envelope emitted, then the worker narrates in a fresh
    // final message. The result field carries only the narration; the envelope (and the
    // block) must still be found in the transcript instead of failing EnvelopeMissing.
    [Fact]
    public void ParseStdoutEnvelope_TrailingNarrationAfterEnvelope_EnvelopeAndBlockFound()
    {
        var stdout = ReadFixture("stream-json-fable-trailing-narration.ndjson");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("Derived project toolchain profile", result.Summary);
        Assert.NotNull(result.Blocks);
        Assert.True(result.Blocks.ContainsKey("PROJECT_PROFILE"));
    }

    // Verbatim capture of a real `claude --model claude-fable-5` derivation run
    // (2026-06-10, the sample-run-14 op-doc). Single-message shape: block + envelope in one
    // assistant message, plus an interleaved rate_limit_event and an empty thinking
    // block. Pins the happy path against the genuine wire format.
    [Fact]
    public void ParseStdoutEnvelope_RealFableCapture_ParsesBlockAndEnvelope()
    {
        var stdout = ReadFixture("stream-json-fable-single-message.ndjson");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "");

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("Derived project toolchain profile", result.Summary);
        Assert.NotNull(result.Blocks);
        Assert.True(result.Blocks.ContainsKey("PROJECT_PROFILE"));
        Assert.Contains("\"language\"", result.Blocks["PROJECT_PROFILE"]);
    }

    [Fact]
    public void ParseStdoutEnvelope_RealFableCapture_ModelExtractedFromSystemEvent()
    {
        var stdout = ReadFixture("stream-json-fable-single-message.ndjson");

        var model = ClaudeCodeAgent.TryExtractModelFromStream(stdout);

        Assert.Equal("claude-fable-5", model);
    }

    // llm_usage must keep coming from the terminal result envelope (cost, cumulative
    // usage), not from the transcript reconstruction.
    [Fact]
    public void ParseStdoutEnvelope_SplitBlockAndEnvelope_LlmUsageMergedFromEnvelope()
    {
        var stdout = ReadFixture("stream-json-fable-split-block.ndjson");

        var result = ClaudeCodeAgent.ParseStdoutEnvelope(stdout, exitCode: 0, stderr: "", wallClockMs: 7, fallbackModel: "fable");

        Assert.True(result.Metadata.TryGetValue("llm_usage", out var usageObj));
        var usage = Assert.IsType<Dictionary<string, object>>(usageObj);
        Assert.Equal("claude-fable-5", usage["model"]);
        Assert.Equal(0.00021, (double)usage["cost_usd"], precision: 10);
    }

    [Fact]
    public void TryExtractAssistantTranscript_SplitFixture_ConcatenatesTextBlocksInOrder()
    {
        var stdout = ReadFixture("stream-json-fable-split-block.ndjson");

        var transcript = ClaudeCodeAgent.TryExtractAssistantTranscript(stdout);

        Assert.NotNull(transcript);
        var blockIdx = transcript.IndexOf("<<<PROJECT_PROFILE_START", StringComparison.Ordinal);
        var markerIdx = transcript.IndexOf("WORKER_RESULT", StringComparison.Ordinal);
        Assert.True(blockIdx >= 0, "transcript must contain the fenced block");
        Assert.True(markerIdx > blockIdx, "envelope marker must follow the block in stream order");
    }

    // Legacy --output-format json single-blob stdout has no assistant NDJSON lines;
    // the transcript is null and the caller falls back to the envelope's result field.
    [Fact]
    public void TryExtractAssistantTranscript_LegacySingleBlob_ReturnsNull()
    {
        var stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false," +
            "\"result\":\"WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"done\\\"}\"}";

        var transcript = ClaudeCodeAgent.TryExtractAssistantTranscript(stdout);

        Assert.Null(transcript);
    }

    // Thinking blocks (including Fable's empty-text thinking blocks) and tool_use blocks
    // must not leak into the transcript.
    [Fact]
    public void TryExtractAssistantTranscript_SkipsThinkingAndToolUseBlocks()
    {
        var stdout =
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"secret reasoning\"}]}}\n" +
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Read\",\"input\":{}}]}}\n" +
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"content\":[{\"type\":\"text\",\"text\":\"visible text\"}]}}\n";

        var transcript = ClaudeCodeAgent.TryExtractAssistantTranscript(stdout);

        Assert.Equal("visible text", transcript);
    }
}
