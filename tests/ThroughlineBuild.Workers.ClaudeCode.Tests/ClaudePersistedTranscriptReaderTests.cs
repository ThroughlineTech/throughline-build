using System.Text.Json;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ClaudePersistedTranscriptReaderTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Read_InstalledVersionFixture_ReconstructsAllTextAndTelemetry()
    {
        var transcript = ClaudePersistedTranscriptReader.Read(
            FixturePath("persisted-transcript-2.1.52.jsonl"), "fixture-session");

        Assert.Contains("<<<REPORT_START", transcript.AssistantTranscript);
        Assert.Contains("WORKER_RESULT", transcript.AssistantTranscript);
        Assert.Equal("claude-sonnet-4-6", transcript.Model);
        Assert.Equal("2.1.52", transcript.ClaudeCodeVersion);
        Assert.Equal(17, transcript.Usage!.InputTokens);
        Assert.Equal(65, transcript.Usage.OutputTokens);
        Assert.Equal(240, transcript.Usage.CacheReadInputTokens);
        Assert.Equal(35, transcript.Usage.CacheCreationInputTokens);
        Assert.Equal(0, transcript.SkippedLines);
    }

    [Fact]
    public void Read_UnknownEventsAndMissingOptionalFields_AreTolerated()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"type":"unknown","sessionId":"s"}
                {"type":"assistant","sessionId":"s","message":{"content":[{"type":"text","text":"hello"}]}}
                """);

            var transcript = ClaudePersistedTranscriptReader.Read(path, "s");

            Assert.Equal("hello", transcript.AssistantTranscript);
            Assert.Null(transcript.Model);
            Assert.Null(transcript.Usage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_DebugEvidence_RedactsSensitiveJsonProperties()
    {
        var transcript = ClaudePersistedTranscriptReader.Read(
            FixturePath("persisted-transcript-2.1.52.jsonl"), "fixture-session");

        Assert.DoesNotContain("fixture-secret", transcript.RedactedRawTranscript);
        Assert.Contains("[REDACTED]", transcript.RedactedRawTranscript);
        Assert.Contains("\"input_tokens\":10", transcript.RedactedRawTranscript);
        foreach (var line in transcript.RedactedNormalizedLines)
            JsonDocument.Parse(line.Line).Dispose();
    }

    [Theory]
    [InlineData("Authorization: Bearer sk-ant-TOPSECRETvalue123456")]
    [InlineData("ANTHROPIC_API_KEY=sk-ant-TOPSECRETvalue123456")]
    [InlineData("api_key: sk-ant-TOPSECRETvalue123456")]
    [InlineData("export TOKEN=sk-ant-TOPSECRETvalue123456")]
    [InlineData("{\"env\":\"sk-ant-TOPSECRETvalue123456\"}")]
    public void RedactText_RemovesCredentialValuesNotJustTheKeyword(string input)
    {
        var redacted = ClaudePersistedTranscriptReader.RedactText(input);

        Assert.DoesNotContain("TOPSECRETvalue123456", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactText_PreservesUsageCountersAndOrdinaryText()
    {
        Assert.Equal("the output_tokens recorded were fine",
            ClaudePersistedTranscriptReader.RedactText("the output_tokens recorded were fine"));
        Assert.Equal("\"input_tokens\":10",
            ClaudePersistedTranscriptReader.RedactText("\"input_tokens\":10"));
    }

    [Fact]
    public void Read_CredentialsInToolValueSubstrings_AreRedacted()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"type":"assistant","sessionId":"s","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"curl -H 'Authorization: Bearer sk-ant-LEAKEDvalue1234567' https://x"}}]}}
                {"type":"user","sessionId":"s","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ANTHROPIC_API_KEY=sk-ant-LEAKEDvalue1234567"}]}}
                """);

            var transcript = ClaudePersistedTranscriptReader.Read(path, "s");

            Assert.DoesNotContain("LEAKEDvalue1234567", transcript.RedactedRawTranscript);
            Assert.Contains("[REDACTED]", transcript.RedactedRawTranscript);
            foreach (var line in transcript.RedactedNormalizedLines)
                JsonDocument.Parse(line.Line).Dispose();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_MissingUsage_RemainsUnavailable()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"assistant\",\"sessionId\":\"s\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}");

            var transcript = ClaudePersistedTranscriptReader.Read(path, "s");

            Assert.Null(transcript.Usage);
        }
        finally { File.Delete(path); }
    }
}
