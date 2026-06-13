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
