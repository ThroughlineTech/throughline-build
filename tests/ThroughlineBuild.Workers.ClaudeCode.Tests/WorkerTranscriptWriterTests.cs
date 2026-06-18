using System.Text.Json;
using Xunit;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

// Exercises the structured --debug transcript writer against the real captured NDJSON
// fixtures. The writer is AOT-safe by construction: it parses with JsonDocument and re-emits
// with Utf8JsonWriter, never touching reflection-based JsonSerializer - so the
// IsReflectionEnabledByDefault switch is irrelevant here and is deliberately NOT flipped (this
// assembly's envelope-parser test helpers use reflection-based JsonSerializer.Serialize, and
// the switch is process-wide).
public class WorkerTranscriptWriterTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Assign deterministic per-line arrival timestamps (base + lineIndex seconds) so dt_ms is
    // a stable function of line position rather than wall clock.
    private static (List<(DateTimeOffset, string)> Lines, DateTimeOffset Base) LoadTimestamped(string fixture)
    {
        var baseTime = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var raw = File.ReadAllLines(FixturePath(fixture));
        var lines = new List<(DateTimeOffset, string)>();
        for (int i = 0; i < raw.Length; i++)
            lines.Add((baseTime.AddSeconds(i), raw[i]));
        return (lines, baseTime);
    }

    private static Brief MakeBrief(
        string ticket = "TLB-999",
        Phase phase = Phase.Implement,
        string instruction = "do the thing",
        string[]? relevantFiles = null,
        string[]? allowedWrites = null)
        => new Brief(ticket, phase, instruction,
            relevantFiles ?? Array.Empty<string>(),
            allowedWrites ?? Array.Empty<string>(),
            new Dictionary<string, string>());

    private static WorkerResult OkResult(params string[] filesChanged)
        => new WorkerResult(Status.Ok, "done", filesChanged, null, new Dictionary<string, object>());

    // Parse the JSONL output into one JsonElement per line.
    private static List<JsonElement> ParseLines(string jsonl)
    {
        var docs = new List<JsonElement>();
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var d = JsonDocument.Parse(line);
            docs.Add(d.RootElement.Clone());
        }
        return docs;
    }

    private static IEnumerable<JsonElement> OfRec(List<JsonElement> recs, string rec)
        => recs.Where(r => r.GetProperty("rec").GetString() == rec);

    [Fact]
    public void BuildJsonl_ToolsFixture_FirstLineIsMetaWithKeys()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");
        var brief = MakeBrief(ticket: "TLB-321", phase: Phase.Implement,
            instruction: "read a file",
            relevantFiles: new[] { "src/a.cs", "src/b.cs" },
            allowedWrites: new[] { "src/a.cs" });

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            brief, lines, OkResult(), new DebugTranscriptContext(BuildVersion: "0.1.0+abc1234", SessionId: "sess-1", ReworkRound: 2),
            model: "claude-opus-4-6",
            invocationArgs: new[] { "--print", "--model", "claude-opus-4-6" },
            wallClockMs: 13538, startedAt: baseTime);

        var recs = ParseLines(jsonl);
        var meta = recs[0];

        Assert.Equal("meta", meta.GetProperty("rec").GetString());
        Assert.Equal(1, meta.GetProperty("schema").GetInt32());
        Assert.Equal("TLB-321", meta.GetProperty("ticket").GetString());
        Assert.Equal("Implement", meta.GetProperty("phase").GetString());
        Assert.Equal("0.1.0+abc1234", meta.GetProperty("build_version").GetString());
        Assert.Equal("sess-1", meta.GetProperty("session_id").GetString());
        Assert.Equal(2, meta.GetProperty("rework_round").GetInt32());
        Assert.Equal("claude-opus-4-6", meta.GetProperty("model").GetString());
        Assert.Equal("2.1.52", meta.GetProperty("claude_code_version").GetString());
        Assert.Equal("worker-stdin.txt", meta.GetProperty("prompt_file").GetString());
        Assert.Equal("read a file".Length, meta.GetProperty("prompt_chars").GetInt32());
        Assert.Equal(64, meta.GetProperty("prompt_sha256").GetString()!.Length); // sha256 hex
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" },
            meta.GetProperty("brief_named_files").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "src/a.cs" },
            meta.GetProperty("brief_named_writes").EnumerateArray().Select(e => e.GetString()).ToArray());
        // session_tools captured verbatim from the system event
        Assert.Equal(JsonValueKind.Array, meta.GetProperty("session_tools").ValueKind);
    }

    [Fact]
    public void BuildJsonl_ToolsFixture_GroupsAssistantLinesIntoThreeTurns()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "claude-opus-4-6", Array.Empty<string>(), 0, baseTime);

        var turns = OfRec(ParseLines(jsonl), "turn").ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal(0, turns[0].GetProperty("i").GetInt32());
        Assert.Equal(1, turns[1].GetProperty("i").GetInt32());
        Assert.Equal(2, turns[2].GetProperty("i").GetInt32());

        // Turn 0 == msg_01789..., one Read tool_use + a thinking block -> discovery.
        Assert.Equal("discovery", turns[0].GetProperty("class").GetString());
        Assert.Equal(1, turns[0].GetProperty("tool_count").GetInt32());
        Assert.Equal("Read", turns[0].GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.True(turns[0].GetProperty("thinking_chars").GetInt32() > 0);

        // Turn 2 == msg_01Saj..., text only, no tools -> respond.
        Assert.Equal("respond", turns[2].GetProperty("class").GetString());
        Assert.Equal(0, turns[2].GetProperty("tool_count").GetInt32());
        Assert.True(turns[2].GetProperty("text_chars").GetInt32() > 0);
    }

    [Fact]
    public void BuildJsonl_ToolsFixture_PerTurnUsageMatchesStream()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "x", Array.Empty<string>(), 0, baseTime);

        var turns = OfRec(ParseLines(jsonl), "turn").ToList();

        // Turn 0: input 3, output 39, cache_read 12695, cache_creation 3288.
        var u0 = turns[0].GetProperty("usage");
        Assert.Equal(3, u0.GetProperty("input").GetInt64());
        Assert.Equal(39, u0.GetProperty("output").GetInt64());
        Assert.Equal(12695, u0.GetProperty("cache_read").GetInt64());
        Assert.Equal(3288, u0.GetProperty("cache_creation").GetInt64());

        // Turn 1 carries a LARGER cache_read (context grew) - the exact "bigger context per
        // turn" vs "more turns" separation this transcript exists to expose.
        Assert.Equal(15983, turns[1].GetProperty("usage").GetProperty("cache_read").GetInt64());
        Assert.Equal(16125, turns[2].GetProperty("usage").GetProperty("cache_read").GetInt64());
    }

    [Fact]
    public void BuildJsonl_ToolsFixture_PerTurnLatencyFromArrivalTimestamps()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "x", Array.Empty<string>(), 0, baseTime);

        var turns = OfRec(ParseLines(jsonl), "turn").ToList();
        // Turn 0 first line is fixture line index 1 -> base+1s. dt from startedAt(base) = 1000ms.
        Assert.Equal(1000, turns[0].GetProperty("dt_ms").GetInt64());
        // Turn 1 first line is index 5 -> base+5s. dt from turn 0 (base+1s) = 4000ms.
        Assert.Equal(4000, turns[1].GetProperty("dt_ms").GetInt64());
    }

    [Fact]
    public void BuildJsonl_ToolsFixture_EmitsToolResultsWithSizeAndErrorFlag()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "x", Array.Empty<string>(), 0, baseTime);

        var results = OfRec(ParseLines(jsonl), "tool_result").ToList();
        Assert.Equal(2, results.Count);

        // First Read failed (file does not exist) -> is_error true.
        Assert.True(results[0].GetProperty("is_error").GetBoolean());
        Assert.Equal("toolu_0167M6ntP6q8CvT5ABi2ozFi", results[0].GetProperty("for").GetString());
        Assert.True(results[0].GetProperty("bytes").GetInt32() > 0);

        // Second Read succeeded -> is_error false, has lines.
        Assert.False(results[1].GetProperty("is_error").GetBoolean());
        Assert.True(results[1].GetProperty("lines").GetInt32() >= 1);
    }

    [Fact]
    public void BuildJsonl_ToolsFixture_ResultRecordCarriesCumulativeUsageAndFiles()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-tools.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult("touched.cs"), null, "x", Array.Empty<string>(), wallClockMs: 13538, startedAt: baseTime);

        var result = Assert.Single(OfRec(ParseLines(jsonl), "result").ToList());
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("Ok", result.GetProperty("worker_status").GetString());
        Assert.Equal(3, result.GetProperty("num_turns").GetInt64());
        Assert.Equal(13538, result.GetProperty("wall_clock_ms").GetInt64());
        Assert.Equal(0.05227025, result.GetProperty("cost_usd").GetDouble(), 8);

        var u = result.GetProperty("usage");
        Assert.Equal(5, u.GetProperty("input").GetInt64());
        Assert.Equal(289, u.GetProperty("output").GetInt64());
        Assert.Equal(44803, u.GetProperty("cache_read").GetInt64());

        // Two distinct Read targets become files_read; the worker self-report becomes files_changed.
        var read = result.GetProperty("files_read").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("/tmp/test-file-for-fixture.txt", read);
        Assert.Contains("C:/Users/developer/AppData/Local/Temp/test-file-for-fixture.txt", read);
        Assert.Equal(new[] { "touched.cs" },
            result.GetProperty("files_changed").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Empty(result.GetProperty("files_written").EnumerateArray());
    }

    [Fact]
    public void BuildJsonl_WorkerResultOkFixture_SingleTurnAndResult()
    {
        var (lines, baseTime) = LoadTimestamped("stream-json-worker-result-ok.ndjson");

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(phase: Phase.Review), lines, OkResult(), null, "claude-sonnet-4-6", Array.Empty<string>(), 1234, baseTime);

        var recs = ParseLines(jsonl);
        Assert.Equal("Review", recs[0].GetProperty("phase").GetString());
        Assert.Single(OfRec(recs, "turn").ToList());
        var result = Assert.Single(OfRec(recs, "result").ToList());
        Assert.Equal(1, result.GetProperty("num_turns").GetInt64());
        Assert.Equal("respond", OfRec(recs, "turn").First().GetProperty("class").GetString());
    }

    [Fact]
    public void BuildJsonl_TruncatedStream_SynthesizesIncompleteResult()
    {
        // System + one assistant turn, NO terminal result event (killed/timed-out worker).
        var baseTime = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var lines = new List<(DateTimeOffset, string)>
        {
            (baseTime, "{\"type\":\"system\",\"session_id\":\"s\",\"model\":\"m\",\"tools\":[]}"),
            (baseTime.AddSeconds(1), "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Write\",\"input\":{\"file_path\":\"x.cs\"}}],\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}"),
        };

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, new WorkerResult(Status.Failed, "timed out", Array.Empty<string>(), "timeout", new Dictionary<string, object>()),
            null, "m", Array.Empty<string>(), 5000, baseTime);

        var recs = ParseLines(jsonl);
        var turn = Assert.Single(OfRec(recs, "turn").ToList());
        Assert.Equal("production", turn.GetProperty("class").GetString());
        var result = Assert.Single(OfRec(recs, "result").ToList());
        Assert.Equal("incomplete", result.GetProperty("status").GetString());
        Assert.Equal("Failed", result.GetProperty("worker_status").GetString());
        Assert.Equal(new[] { "x.cs" },
            result.GetProperty("files_written").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void BuildJsonl_MalformedLines_AreCountedNotFatal()
    {
        var baseTime = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var lines = new List<(DateTimeOffset, string)>
        {
            (baseTime, "{\"type\":\"system\",\"session_id\":\"s\",\"model\":\"m\"}"),
            (baseTime.AddSeconds(1), "this is not json"),
            (baseTime.AddSeconds(2), "{ broken json"),
            (baseTime.AddSeconds(3), "{\"type\":\"result\",\"is_error\":false,\"num_turns\":0,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
        };

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "m", Array.Empty<string>(), 0, baseTime);

        var result = Assert.Single(OfRec(ParseLines(jsonl), "result").ToList());
        Assert.Equal(2, result.GetProperty("skipped_lines").GetInt32());
    }

    [Fact]
    public void BuildJsonl_ToolInputPreservedVerbatim()
    {
        var baseTime = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);
        var lines = new List<(DateTimeOffset, string)>
        {
            (baseTime, "{\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Grep\",\"input\":{\"pattern\":\"foo.*bar\",\"glob\":\"*.cs\"}}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}"),
            (baseTime.AddSeconds(1), "{\"type\":\"result\",\"is_error\":false,\"num_turns\":1,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
        };

        var jsonl = WorkerTranscriptWriter.BuildJsonl(
            MakeBrief(), lines, OkResult(), null, "m", Array.Empty<string>(), 0, baseTime);

        var turn = OfRec(ParseLines(jsonl), "turn").First();
        var input = turn.GetProperty("tools")[0].GetProperty("input");
        Assert.Equal("foo.*bar", input.GetProperty("pattern").GetString());
        Assert.Equal("*.cs", input.GetProperty("glob").GetString());
        // Grep is a search, not a file read: must NOT appear in files_read.
        var result = OfRec(ParseLines(jsonl), "result").First();
        Assert.Empty(result.GetProperty("files_read").EnumerateArray());
    }

    [Theory]
    [InlineData("Read", "discovery")]
    [InlineData("Grep", "discovery")]
    [InlineData("Glob", "discovery")]
    [InlineData("Write", "production")]
    [InlineData("Edit", "production")]
    [InlineData("Bash", "verification")]
    public void ClassifyTurn_SingleTool_MapsToExpectedClass(string tool, string expected)
    {
        var tools = new List<(string, string)> { (tool, "{}") };
        Assert.Equal(expected, WorkerTranscriptWriter.ClassifyTurn(tools, textChars: 0, thinkingChars: 0));
    }

    [Fact]
    public void ClassifyTurn_ProductionWinsOverDiscovery_WhenMixed()
    {
        var tools = new List<(string, string)> { ("Read", "{}"), ("Edit", "{}") };
        Assert.Equal("production", WorkerTranscriptWriter.ClassifyTurn(tools, 0, 0));
    }

    [Fact]
    public void ClassifyTurn_NoTools_ThinkingOnly_IsReason()
    {
        Assert.Equal("reason", WorkerTranscriptWriter.ClassifyTurn(new(), textChars: 0, thinkingChars: 50));
        Assert.Equal("respond", WorkerTranscriptWriter.ClassifyTurn(new(), textChars: 10, thinkingChars: 0));
    }

    [Fact]
    public void Write_CreatesTranscriptFileInCaptureDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb-transcript-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (lines, baseTime) = LoadTimestamped("stream-json-worker-result-ok.ndjson");
            WorkerTranscriptWriter.Write(dir, MakeBrief(), lines, OkResult(), null, "m", Array.Empty<string>(), 0, baseTime);

            var path = Path.Combine(dir, WorkerTranscriptWriter.FileName);
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.StartsWith("{\"rec\":\"meta\"", text);
            Assert.EndsWith("\n", text);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
