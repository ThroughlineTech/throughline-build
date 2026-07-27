using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

// Exercises the per-turn context-attribution parser. The parser is JsonDocument-based (no
// reflection-based JsonSerializer), so the IsReflectionEnabledByDefault switch is irrelevant here
// and is deliberately NOT flipped (same rationale as WorkerTranscriptWriterTests).
//
// LEAK-PROOF FIXTURES: the NDJSON strings below contain ZERO stack tokens - no file extensions,
// no language names, no framework/tool-runner names. They use claude-code vendor tool NAMES only
// (Read/Write/TodoWrite/Task/Bash) with neutral/empty inputs. This is the no-single-stack-leak proof:
// the parser observes only tool names and token counts, never anything stack-specific.
public class ClaudeCodeTurnParserTests
{
    // One assistant message per line, single-class tool_use, neutral empty input.
    private static string AssistantLine(string id, string toolName, long cacheRead, long cacheCreation, long output)
        => "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"role\":\"assistant\","
         + "\"usage\":{\"cache_read_input_tokens\":" + cacheRead
         + ",\"cache_creation_input_tokens\":" + cacheCreation
         + ",\"output_tokens\":" + output + "},"
         + "\"content\":[{\"type\":\"tool_use\",\"name\":\"" + toolName + "\",\"input\":{}}]}}";

    [Fact]
    public void Parse_SeriesAndBuckets_FromStreamOnlyFixture()
    {
        // 4 turns, each a single-class tool_use. The fixture carries no stack tokens (no file
        // extensions, no language names, no framework names) - only vendor tool names + counts.
        var stdout = string.Join("\n", new[]
        {
            AssistantLine("m1", "Read", 1000, 50, 10),
            AssistantLine("m2", "Write", 2000, 200, 20),
            AssistantLine("m3", "TodoWrite", 3000, 30, 5),
            AssistantLine("m4", "Task", 4000, 40, 8),
        });

        var series = ClaudeCodeTurnParser.Parse(stdout);

        Assert.Equal(new long[] { 1000, 2000, 3000, 4000 }, series.CacheReadSeries);
        Assert.Equal(new long[] { 50, 200, 30, 40 }, series.CacheCreationSeries);
        Assert.Equal(new long[] { 10, 20, 5, 8 }, series.OutputSeries);
        Assert.Equal(4, series.Turns);
        Assert.Equal(50L, series.ReadBytes);
        Assert.Equal(200L, series.WriteBytes);
        Assert.Equal(30L, series.TodoBytes);
        Assert.Equal(40L, series.TaskBytes);
        Assert.Equal(0L, series.BashBytes);
        Assert.Equal(0L, series.OtherBytes);
        Assert.Equal(10000L, series.TotalCacheRead);
        // Turns < 8 -> slope is the undefined sentinel.
        Assert.Equal(-1.0, series.SlopeRatio);
    }

    [Fact]
    public void ComputeSlopeRatio_EightPlusTurns_RatioOfLast5AvgToFirst3Avg()
    {
        // first 3 (indices 0,1,2) = [10,20,30] -> avg 20.
        // last 5 (indices 3..7)   = [60,70,80,90,100] -> sum 400 -> avg 80.
        // 80 / 20 == 4.0 exactly.
        var list = new List<long> { 10, 20, 30, 60, 70, 80, 90, 100 };
        Assert.Equal(4.0, ClaudeCodeTurnParser.ComputeSlopeRatio(list));
    }

    [Fact]
    public void ComputeSlopeRatio_FewerThan8Turns_ReturnsMinusOne()
    {
        var list = new List<long> { 1, 2, 3, 4, 5, 6, 7 };
        Assert.Equal(-1.0, ClaudeCodeTurnParser.ComputeSlopeRatio(list));
    }

    [Fact]
    public void ComputeSlopeRatio_FirstAvgZero_ReturnsMinusOne()
    {
        var list = new List<long> { 0, 0, 0, 60, 70, 80, 90, 100 };
        Assert.Equal(-1.0, ClaudeCodeTurnParser.ComputeSlopeRatio(list));
    }

    [Fact]
    public void Parse_EmptyOrNoAssistantTurns_ReturnsZeroTurns()
    {
        var empty = ClaudeCodeTurnParser.Parse("");
        Assert.Equal(0, empty.Turns);
        Assert.Empty(empty.CacheReadSeries);
        Assert.Empty(empty.CacheCreationSeries);
        Assert.Empty(empty.OutputSeries);
        Assert.Equal(0L, empty.TotalCacheRead);
        Assert.Equal(-1.0, empty.SlopeRatio);

        // A stream carrying only a non-assistant (system) event also yields zero turns.
        var systemOnly = ClaudeCodeTurnParser.Parse(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\"}");
        Assert.Equal(0, systemOnly.Turns);
        Assert.Empty(systemOnly.CacheReadSeries);
        Assert.Equal(-1.0, systemOnly.SlopeRatio);
    }

    [Fact]
    public void Parse_DedupsUsageAcrossMultiLineMessage()
    {
        // One assistant message "m1" emitted as two NDJSON lines sharing the same id and the same
        // usage object: a thinking-only line then a tool_use line. It must count as ONE turn with the
        // usage counted once.
        var thinkingLine =
            "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"role\":\"assistant\","
          + "\"usage\":{\"cache_read_input_tokens\":1000,\"cache_creation_input_tokens\":50,\"output_tokens\":10},"
          + "\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"}]}}";
        var toolLine = AssistantLine("m1", "Read", 1000, 50, 10);
        var stdout = thinkingLine + "\n" + toolLine;

        var series = ClaudeCodeTurnParser.Parse(stdout);

        Assert.Equal(1, series.Turns);
        Assert.Equal(new long[] { 1000 }, series.CacheReadSeries);
        Assert.Equal(new long[] { 50 }, series.CacheCreationSeries);
        Assert.Equal(new long[] { 10 }, series.OutputSeries);
        Assert.Equal(1000L, series.TotalCacheRead);
        // The tool_use (Read) on the second line classifies the turn -> read bucket.
        Assert.Equal(50L, series.ReadBytes);
        Assert.Equal(0L, series.WriteBytes);
    }
}
