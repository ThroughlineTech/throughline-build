using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

/// <summary>
/// Tests for ReviewFeedbackRetriever.GetLatestRework.
/// Each test creates a temp directory with hand-crafted JSONL lines
/// (matching the on-disk schema emitted by JsonlEventSink + EventLineDto),
/// invokes the retriever, asserts the outcome, and cleans up.
/// </summary>
public class ReviewFeedbackRetrieverTests
{
    // EventKind.VerifierVerdict == 3  (from the enum definition)
    private const int VerifierVerdictKind = 3;
    // Phase.Review == 2  (Plan=0, Implement=1, Review=2, ...)
    private const int ReviewPhase = 2;

    private static string MakeVerifierVerdictLine(
        string ticketId,
        string verdict,
        string rationale,
        IEnumerable<string>? checksFailed = null,
        int kind = VerifierVerdictKind,
        int phase = ReviewPhase)
    {
        var checks = checksFailed == null
            ? "[]"
            : "[" + string.Join(",", (checksFailed).Select(c => $"\"{c}\"")) + "]";
        return $"{{\"SessionId\":\"s1\",\"Timestamp\":\"2026-05-27T10:00:00+00:00\",\"Kind\":{kind},\"TicketId\":\"{ticketId}\",\"Phase\":{phase},\"Data\":{{\"kind\":\"{verdict}\",\"checks_failed_count\":0,\"rationale\":\"{rationale}\",\"checks_failed\":{checks}}}}}";
    }

    private static string MakeOtherEventLine(string ticketId) =>
        $"{{\"SessionId\":\"s1\",\"Timestamp\":\"2026-05-27T09:00:00+00:00\",\"Kind\":0,\"TicketId\":\"{ticketId}\",\"Phase\":2,\"Data\":{{}}}}";

    // -----------------------------------------------------------------------

    [Fact]
    public void GetLatestRework_ReworkVerdict_ReturnsReviewFeedback()
    {
        var dir = MakeTempDir();
        try
        {
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeOtherEventLine("TLB-42"),
                MakeVerifierVerdictLine("TLB-42", "Rework", "missing tests", new[] { "build", "lint" }));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.NotNull(result);
            Assert.Equal("missing tests", result!.Rationale);
            Assert.Equal(new[] { "build", "lint" }, result.ChecksFailed);
            Assert.Equal(1, result.ReworkRoundNumber);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_PassVerdict_ReturnsNull()
    {
        var dir = MakeTempDir();
        try
        {
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeVerifierVerdictLine("TLB-42", "Pass", "looks good"));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.Null(result);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_FailVerdict_ReturnsNull()
    {
        var dir = MakeTempDir();
        try
        {
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeVerifierVerdictLine("TLB-42", "Fail", "fundamental issue"));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.Null(result);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_NoEventForTicket_ReturnsNull()
    {
        var dir = MakeTempDir();
        try
        {
            // Events exist but for a different ticket.
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeVerifierVerdictLine("TLB-99", "Rework", "something"));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.Null(result);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_MultipleSessionFiles_ReturnsMostRecent()
    {
        var dir = MakeTempDir();
        try
        {
            // Older session: Rework with "old rationale"
            var olderFile = Path.Combine(dir, "session-old.jsonl");
            WriteLines(olderFile,
                MakeVerifierVerdictLine("TLB-42", "Rework", "old rationale"));

            // Newer session: Pass  (should win and cause null return)
            var newerFile = Path.Combine(dir, "session-new.jsonl");
            WriteLines(newerFile,
                MakeVerifierVerdictLine("TLB-42", "Pass", "looks good now"));

            // Force the file system to treat session-new as newer.
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(olderFile, baseTime);
            File.SetLastWriteTimeUtc(newerFile, baseTime.AddHours(1));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            // Most recent review is Pass -> null
            Assert.Null(result);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_MultipleSessionFiles_OlderReworkNotMaskedByNewerOtherTicket()
    {
        var dir = MakeTempDir();
        try
        {
            // Older session: Rework for TLB-42
            var olderFile = Path.Combine(dir, "session-old.jsonl");
            WriteLines(olderFile,
                MakeVerifierVerdictLine("TLB-42", "Rework", "my rationale", new[] { "test-x" }));

            // Newer session: verdict for a DIFFERENT ticket - should not interfere
            var newerFile = Path.Combine(dir, "session-new.jsonl");
            WriteLines(newerFile,
                MakeVerifierVerdictLine("TLB-99", "Pass", "unrelated"));

            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(olderFile, baseTime);
            File.SetLastWriteTimeUtc(newerFile, baseTime.AddHours(1));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            // Newer file has no event for TLB-42 so we fall through to older file
            Assert.NotNull(result);
            Assert.Equal("my rationale", result!.Rationale);
            Assert.Equal(new[] { "test-x" }, result.ChecksFailed);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_MalformedJsonlLine_SkipsAndReturnsValidResult()
    {
        var dir = MakeTempDir();
        try
        {
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeVerifierVerdictLine("TLB-42", "Rework", "found it"),
                "{{not valid json}}",
                MakeOtherEventLine("TLB-42"));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            // Malformed line is skipped; valid Rework line is found.
            Assert.NotNull(result);
            Assert.Equal("found it", result!.Rationale);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_MissingEventsDirectory_ReturnsNull()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "events");
        var retriever = new ReviewFeedbackRetriever(nonExistentDir);

        var result = retriever.GetLatestRework("TLB-42");

        Assert.Null(result);
    }

    [Fact]
    public void GetLatestRework_EmptyDirectory_ReturnsNull()
    {
        var dir = MakeTempDir();
        try
        {
            // No .jsonl files at all
            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.Null(result);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetLatestRework_ReworkWithEmptyChecksFailed_ReturnsEmptyList()
    {
        var dir = MakeTempDir();
        try
        {
            WriteLines(Path.Combine(dir, "session1.jsonl"),
                MakeVerifierVerdictLine("TLB-42", "Rework", "refactor needed", Array.Empty<string>()));

            var retriever = new ReviewFeedbackRetriever(dir);
            var result = retriever.GetLatestRework("TLB-42");

            Assert.NotNull(result);
            Assert.Equal("refactor needed", result!.Rationale);
            Assert.Empty(result.ChecksFailed);
            Assert.Equal(1, result.ReworkRoundNumber);
        }
        finally { Cleanup(dir); }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteLines(string path, params string[] lines)
    {
        File.WriteAllLines(path, lines);
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
