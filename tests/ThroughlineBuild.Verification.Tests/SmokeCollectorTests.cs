using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Verification;
using Xunit;

namespace ThroughlineBuild.Verification.Tests;

public class SmokeCollectorTests
{
    private readonly SmokeCollector _collector = new();

    // ---- DiffFacts ----

    [Fact]
    public void CollectDiffFacts_EmptyDiff_IsSmoke_AlwaysMatched()
    {
        var diff = MakeDiff();
        var signal = _collector.CollectDiffFacts(diff);

        Assert.Equal(SmokeSignalKind.DiffFacts, signal.Kind);
        Assert.Equal("diff-facts", signal.Label);
        Assert.True(signal.Matched, "DiffFacts is always Matched=true");
        Assert.Contains("0 file(s)", signal.Details);
    }

    [Fact]
    public void CollectDiffFacts_ReportsFilesAndLineDelta()
    {
        var diff = MakeDiff(
            Entry("src/Foo.cs", DiffKind.Added, linesAdded: 10),
            Entry("src/Bar.cs", DiffKind.Modified, linesAdded: 5, linesRemoved: 3));

        var signal = _collector.CollectDiffFacts(diff);

        Assert.Equal(SmokeSignalKind.DiffFacts, signal.Kind);
        Assert.True(signal.Matched);
        Assert.Contains("2 file(s)", signal.Details);
        Assert.Contains("+15", signal.Details);
        Assert.Contains("-3", signal.Details);
    }

    [Fact]
    public void CollectDiffFacts_WithTestFile_DetectsPresence()
    {
        var diff = MakeDiff(
            Entry("src/Foo.cs", DiffKind.Added),
            Entry("tests/FooTests.cs", DiffKind.Added));

        var signal = _collector.CollectDiffFacts(diff);

        Assert.Contains("Test files present", signal.Details);
        Assert.Contains("FooTests.cs", signal.Details);
    }

    [Fact]
    public void CollectDiffFacts_NoTestFile_SaysAbsent()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Added));

        var signal = _collector.CollectDiffFacts(diff);

        Assert.Contains("No test files in diff", signal.Details);
    }

    // ---- GrepPresentInDiff ----

    [Fact]
    public void GrepPresentInDiff_PatternFound_MatchedTrue()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Modified, patch: "+// TODO: implement\n+int x = 1;\n"));

        var signal = _collector.GrepPresentInDiff("todo", "TODO", diff);

        Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
        Assert.True(signal.Matched);
        Assert.Contains("1 match", signal.Details);
    }

    [Fact]
    public void GrepPresentInDiff_PatternNotFound_MatchedFalse()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Modified, patch: "+int x = 1;\n"));

        var signal = _collector.GrepPresentInDiff("todo", "TODO", diff);

        Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
        Assert.False(signal.Matched);
        Assert.Contains("not found", signal.Details);
    }

    [Fact]
    public void GrepPresentInDiff_NullPatch_HandledGracefully_NotMatched()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Modified, patch: null));

        var signal = _collector.GrepPresentInDiff("todo", "TODO", diff);

        Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
        Assert.False(signal.Matched);
        // Notes the missing patch
        Assert.Contains("no patch content", signal.Details);
    }

    [Fact]
    public void GrepPresentInDiff_MultipleEntries_CountsAllMatches()
    {
        var diff = MakeDiff(
            Entry("src/Foo.cs", DiffKind.Modified, patch: "+// TODO: one\n+// TODO: two\n"),
            Entry("src/Bar.cs", DiffKind.Modified, patch: "+// TODO: three\n"));

        var signal = _collector.GrepPresentInDiff("todo", "TODO", diff);

        Assert.True(signal.Matched);
        Assert.Contains("3 match", signal.Details);
        Assert.Contains("2 file", signal.Details);
    }

    // ---- GrepAbsentInDiff ----

    [Fact]
    public void GrepAbsentInDiff_PatternAbsent_MatchedTrue_Clean()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Modified, patch: "+int x = 1;\n"));

        var signal = _collector.GrepAbsentInDiff("stub", "STUB_PLACEHOLDER", diff);

        Assert.Equal(SmokeSignalKind.GrepAbsent, signal.Kind);
        Assert.True(signal.Matched, "pattern absent = clean");
        Assert.Contains("absent", signal.Details);
    }

    [Fact]
    public void GrepAbsentInDiff_PatternPresent_MatchedFalse_Suspicious()
    {
        var diff = MakeDiff(Entry("src/Foo.cs", DiffKind.Modified, patch: "+// STUB_PLACEHOLDER\n"));

        var signal = _collector.GrepAbsentInDiff("stub", "STUB_PLACEHOLDER", diff);

        Assert.Equal(SmokeSignalKind.GrepAbsent, signal.Kind);
        Assert.False(signal.Matched, "pattern present = suspicious");
        Assert.Contains("STUB_PLACEHOLDER", signal.Details);
    }

    // ---- Tree grep ----

    [Fact]
    public void GrepPresentInTree_PatternFound_MatchedTrue()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "// TODO: implement");
        try
        {
            var signal = _collector.GrepPresentInTree("todo-tree", "TODO", dir);
            Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
            Assert.True(signal.Matched);
            Assert.Contains("1 match", signal.Details);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GrepPresentInTree_PatternNotFound_MatchedFalse()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "public class Foo { }");
        try
        {
            var signal = _collector.GrepPresentInTree("todo-tree", "TODO", dir);
            Assert.Equal(SmokeSignalKind.GrepPresent, signal.Kind);
            Assert.False(signal.Matched);
            Assert.Contains("not found", signal.Details);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GrepAbsentInTree_PatternAbsent_MatchedTrue()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "public class Foo { }");
        try
        {
            var signal = _collector.GrepAbsentInTree("stub-tree", "STUB_PLACEHOLDER", dir);
            Assert.Equal(SmokeSignalKind.GrepAbsent, signal.Kind);
            Assert.True(signal.Matched, "pattern absent = clean");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GrepAbsentInTree_PatternPresent_MatchedFalse()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "// STUB_PLACEHOLDER");
        try
        {
            var signal = _collector.GrepAbsentInTree("stub-tree", "STUB_PLACEHOLDER", dir);
            Assert.Equal(SmokeSignalKind.GrepAbsent, signal.Kind);
            Assert.False(signal.Matched, "pattern found = suspicious");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GrepPresentInTree_SkipsHiddenDirectories()
    {
        var dir = TempDir();
        var hidden = Path.Combine(dir, ".git");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "COMMIT_EDITMSG"), "TODO: internal git file");
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "public class Foo { }");
        try
        {
            var signal = _collector.GrepPresentInTree("todo-tree", "TODO", dir);
            // .git/COMMIT_EDITMSG is skipped so no match
            Assert.False(signal.Matched, "hidden dir content should be skipped");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Structural distinctness: SmokeSignal is not CheckResult ----

    [Fact]
    public void SmokeSignal_IsDistinctType_FromCheckResult()
    {
        var diff = MakeDiff();
        SmokeSignal facts = _collector.CollectDiffFacts(diff);
        SmokeSignal present = _collector.GrepPresentInDiff("l", "p", diff);
        SmokeSignal absent = _collector.GrepAbsentInDiff("l", "p", diff);

        // Type-check: these are SmokeSignal, not CheckResult or Verdict.
        // The Kind field is SmokeSignalKind, not CheckRole.
        Assert.IsType<SmokeSignal>(facts);
        Assert.IsType<SmokeSignal>(present);
        Assert.IsType<SmokeSignal>(absent);
        Assert.IsAssignableFrom<SmokeSignal>(facts);
    }

    // ---- Helper factories ----

    private static GitDiff MakeDiff(params DiffEntry[] entries) =>
        new("base-sha", "feature-sha", entries);

    private static DiffEntry Entry(
        string path,
        DiffKind kind,
        int linesAdded = 0,
        int linesRemoved = 0,
        string? patch = null) =>
        new(path, kind, null, linesAdded, linesRemoved, patch);

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
