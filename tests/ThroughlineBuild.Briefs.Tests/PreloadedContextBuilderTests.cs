using System.Collections.Generic;
using System.Linq;
using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

/// <summary>
/// Stack-free facts for the pre-load mechanism. No disk: the reader is a fake. Extraction reads the
/// POSITIVE-ONLY Preload block only (never the prose Inputs read-map), keying on the <h3>Preload</h3>
/// heading + TryNormalizeRelPath - never on language - so the second-stack tests (.py / .go) are the
/// primary proof no TypeScript assumption leaked. The telemetry facts prove a no-op pre-load is LOUD
/// (experiment 3): not-found is counted, never pasted into the prompt.
/// </summary>
public class PreloadedContextBuilderTests
{
    private static Func<string, string?> Reader(Dictionary<string, string> files) =>
        path => files.TryGetValue(path, out var c) ? c : null;

    private static ProjectContext Project(params string[] conventionFiles) =>
        ProjectContext.Empty with { ConventionFiles = conventionFiles };

    // Mirrors BriefHtmlRenderer.RenderPreloadList output: <h3>Preload</h3><ul><li><code>path</code></li>...</ul>
    private static string PreloadHtml(params string[] paths)
    {
        var lis = string.Concat(paths.Select(p => $"<li><code>{p}</code></li>"));
        return $"<h3>Goal</h3><p>g</p><h3>Preload</h3><ul>{lis}</ul>";
    }

    // ---- ExtractPreloadPaths ----

    [Fact]
    public void Extract_KeepsValidPaths_RejectsRootedAndRouteAndParenTokens()
    {
        // Positive-only: no symbol-vs-path heuristic. TryNormalizeRelPath alone rejects a rooted path,
        // a route param (colon), and a paren-bearing token - everything else is taken as a path.
        var html = PreloadHtml("src/data/types.ts", "getSurvey(id)", "/responses/:responseId");

        var paths = PreloadedContextBuilder.ExtractPreloadPaths(html);

        Assert.Equal(new[] { "src/data/types.ts" }, paths);
    }

    [Fact]
    public void Extract_OnlyScansThePreloadSection_NotInputsOrOutputs()
    {
        // The prose Inputs read-map (which can name EXCLUSION paths) is structurally never read - only
        // the positive-only Preload block is. This is the experiment-3 "never load an exclusion" guarantee.
        var html = "<h3>Inputs</h3><ul><li><p>do not read <code>src/setupTests.ts</code></p></li></ul>"
                 + "<h3>Preload</h3><ul><li><code>src/keep.ts</code></li></ul>"
                 + "<h3>Outputs</h3><ul><li><p><code>src/out.ts</code></p></li></ul>";

        var paths = PreloadedContextBuilder.ExtractPreloadPaths(html);

        Assert.Equal(new[] { "src/keep.ts" }, paths);
    }

    [Fact]
    public void Extract_NoPreloadSection_ReturnsEmpty()
    {
        Assert.Empty(PreloadedContextBuilder.ExtractPreloadPaths("<h3>Goal</h3><p>g</p><h3>Inputs</h3><ul><li><p><code>src/in.ts</code></p></li></ul>"));
        Assert.Empty(PreloadedContextBuilder.ExtractPreloadPaths(null));
    }

    [Fact]
    public void Extract_SecondStack_PythonAndGoPaths()
    {
        var html = PreloadHtml("src/app/models.py", "internal/store/repo.go");

        var paths = PreloadedContextBuilder.ExtractPreloadPaths(html);

        Assert.Equal(new[] { "src/app/models.py", "internal/store/repo.go" }, paths);
    }

    // ---- Build: inline + not-found telemetry ----

    [Fact]
    public void Build_InlinesDeclaredPath_AndCountsMissingAsTelemetry_NotPromptNoise()
    {
        var html = PreloadHtml("src/data/types.ts", "src/data/repository.ts");
        var files = new Dictionary<string, string> { ["src/data/types.ts"] = "export type Survey = {};" };

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files));

        Assert.Contains("## Pre-loaded context", r.Section);
        Assert.Contains("`src/data/types.ts`", r.Section);
        Assert.Contains("export type Survey = {};", r.Section);
        Assert.StartsWith("\n", r.Section); // leading newline so the template stays inert when empty
        // not-found is COUNTABLE telemetry, never a "(not found)" line in the prompt
        Assert.DoesNotContain("repository.ts", r.Section);
        Assert.DoesNotContain("not found", r.Section);
        Assert.Equal(new[] { "src/data/repository.ts" }, r.NotFoundNamed);
        Assert.Equal(new[] { "src/data/types.ts" }, r.LoadedWhole);
        Assert.Equal(1, r.FilesLoaded);
        Assert.Equal(2, r.FilesRequested);
        Assert.False(r.DeclaredButAllMissing); // one loaded -> not a no-op
    }

    [Fact]
    public void Build_NotFound_SplitsNamedVsConvention_AndFlagsDeclaredButAllMissing()
    {
        var project = Project("conftest.py");          // convention path, will miss
        var html = PreloadHtml("src/named.ts");        // declared Preload path, will miss
        var r = PreloadedContextBuilder.Build(html, project, _ => null); // everything missing

        Assert.Equal(new[] { "src/named.ts" }, r.NotFoundNamed);
        Assert.Equal(new[] { "conftest.py" }, r.NotFoundConvention);
        Assert.Equal(new[] { "src/named.ts", "conftest.py" }, r.NotFoundAll);
        Assert.Equal(string.Empty, r.Section);          // nothing loaded -> inert section
        Assert.Equal(0, r.FilesLoaded);
        Assert.True(r.DeclaredButAllMissing);           // declared Preload, loaded zero -> the preload_empty signal
    }

    [Fact]
    public void Build_ConventionOnlyMissing_IsNotDeclaredButAllMissing_Greenfield()
    {
        var project = Project("conftest.py");           // convention only, missing
        var r = PreloadedContextBuilder.Build(null, project, _ => null); // no Preload block declared

        Assert.Equal(new[] { "conftest.py" }, r.NotFoundConvention);
        Assert.Empty(r.NotFoundNamed);
        Assert.False(r.DeclaredButAllMissing);          // convention-only absence stays quiet (greenfield-expected)
        Assert.Equal(string.Empty, r.Section);
    }

    [Fact]
    public void Build_ConventionFilesFirst_ThenDeclaredPaths_Deduped()
    {
        var html = PreloadHtml("src/data/types.ts", "src/data/repository.ts");
        var project = Project("src/setupTests.ts", "src/data/types.ts"); // types.ts also a declared path
        var files = new Dictionary<string, string>
        {
            ["src/setupTests.ts"] = "setup();",
            ["src/data/types.ts"] = "types;",
            ["src/data/repository.ts"] = "repo;",
        };

        var r = PreloadedContextBuilder.Build(html, project, Reader(files));

        // Order: setupTests (convention) -> types.ts (convention, NOT repeated as declared) -> repository.ts
        int setup = r.Section.IndexOf("`src/setupTests.ts`");
        int types = r.Section.IndexOf("`src/data/types.ts`");
        int repo = r.Section.IndexOf("`src/data/repository.ts`");
        Assert.True(setup >= 0 && types > setup && repo > types, $"order setup={setup} types={types} repo={repo}");
        Assert.Equal(types, r.Section.LastIndexOf("`src/data/types.ts`")); // exactly once
        Assert.Equal(3, r.FilesLoaded);
    }

    [Fact]
    public void Build_NoConventionAndNoDeclaredPaths_ReturnsEmpty()
    {
        var html = "<h3>Goal</h3><p>g</p>"; // no Preload block
        var r1 = PreloadedContextBuilder.Build(html, ProjectContext.Empty, _ => null);
        var r2 = PreloadedContextBuilder.Build(null, ProjectContext.Empty, _ => null);

        Assert.Equal(string.Empty, r1.Section);
        Assert.Equal(0, r1.FilesRequested);
        Assert.False(r1.DeclaredButAllMissing);
        Assert.Equal(string.Empty, r2.Section);
    }

    [Fact]
    public void Build_HtmlEscapedToken_IsUnescapedBeforeMatching()
    {
        var html = PreloadHtml("src/a&amp;b/types.ts");
        var files = new Dictionary<string, string> { ["src/a&b/types.ts"] = "x;" };

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files));

        Assert.Contains("`src/a&b/types.ts`", r.Section);
        Assert.Contains("x;", r.Section);
        Assert.Equal(new[] { "src/a&b/types.ts" }, r.LoadedWhole);
    }

    // ---- Build: bounds telemetry (whole vs truncated vs omitted) ----

    [Fact]
    public void Build_OversizedFile_TruncatedWithMarker_AndCountedAsTruncated()
    {
        var html = PreloadHtml("src/big.ts");
        var big = new string('x', 5000);
        var files = new Dictionary<string, string> { ["src/big.ts"] = big };
        var opts = new PreloadOptions(MaxFiles: 12, MaxCharsPerFile: 1000, MaxTotalChars: 64 * 1024);

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("truncated", r.Section);
        Assert.True(r.Section.Length < 5000, "the 5000-char file must not be inlined whole");
        Assert.Equal(new[] { "src/big.ts" }, r.LoadedTruncated);
        Assert.Empty(r.LoadedWhole);
        Assert.Equal(1, r.FilesTruncated);
        Assert.Equal(1, r.FilesLoaded);
    }

    [Fact]
    public void Build_TotalBudgetExceeded_RemainingFilesOmittedNotSilent()
    {
        var html = PreloadHtml("src/a.ts", "src/b.ts");
        var files = new Dictionary<string, string>
        {
            ["src/a.ts"] = new string('a', 900),
            ["src/b.ts"] = new string('b', 900),
        };
        var opts = new PreloadOptions(MaxFiles: 12, MaxCharsPerFile: 16 * 1024, MaxTotalChars: 1000);

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("`src/a.ts`", r.Section);
        Assert.Contains("omitted (pre-load cap)", r.Section);
        Assert.Contains("`src/b.ts`", r.Section); // listed in the omitted summary
        Assert.DoesNotContain(new string('b', 900), r.Section);
        Assert.Equal(new[] { "src/b.ts" }, r.Omitted);
        Assert.Equal(new[] { "src/a.ts" }, r.LoadedWhole);
    }

    [Fact]
    public void Build_FileCountCap_OverflowListedInOrder()
    {
        var html = PreloadHtml("src/a.ts", "src/b.ts", "src/c.ts");
        var files = new Dictionary<string, string>
        {
            ["src/a.ts"] = "a;",
            ["src/b.ts"] = "b;",
            ["src/c.ts"] = "c;",
        };
        var opts = new PreloadOptions(MaxFiles: 1, MaxCharsPerFile: 16 * 1024, MaxTotalChars: 64 * 1024);

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("a;", r.Section);
        Assert.Contains("omitted (pre-load cap)", r.Section);
        Assert.DoesNotContain("b;", r.Section);
        Assert.DoesNotContain("c;", r.Section);
        Assert.Equal(new[] { "src/b.ts", "src/c.ts" }, r.Omitted);
    }

    // ---- Build: containment (defense in depth) ----

    [Fact]
    public void Build_ParentEscapeAndRootedPaths_NeverReachReaderAndAreNotInlined()
    {
        var html = PreloadHtml("../secret.ts", "/etc/passwd.txt", "src/ok.ts");
        var asked = new List<string>();
        Func<string, string?> reader = p => { asked.Add(p); return p == "src/ok.ts" ? "ok;" : null; };

        var r = PreloadedContextBuilder.Build(html, ProjectContext.Empty, reader);

        Assert.Contains("src/ok.ts", r.Section);
        Assert.DoesNotContain("secret", r.Section);
        Assert.DoesNotContain("passwd", r.Section);
        // The escaping/rooted paths are rejected at the path-validity gate and never reach the reader.
        Assert.Equal(new[] { "src/ok.ts" }, asked);
    }

    // ---- Build: second-stack convention bundle ----

    [Fact]
    public void Build_ConventionFileContent_Inlined_NoLanguageBranch_SecondStack()
    {
        // A python-shaped convention bundle proves the engine carries whatever the deriver chose.
        var project = Project("conftest.py", "tests/test_example.py");
        var html = PreloadHtml("src/app/models.py");
        var files = new Dictionary<string, string>
        {
            ["conftest.py"] = "import pytest",
            ["tests/test_example.py"] = "def test_x(): assert True",
            ["src/app/models.py"] = "class Model: pass",
        };

        var r = PreloadedContextBuilder.Build(html, project, Reader(files));

        int conftest = r.Section.IndexOf("`conftest.py`");
        int example = r.Section.IndexOf("`tests/test_example.py`");
        int models = r.Section.IndexOf("`src/app/models.py`");
        Assert.True(conftest >= 0 && example > conftest && models > example);
        Assert.Contains("import pytest", r.Section);
        Assert.Contains("class Model: pass", r.Section);
        Assert.Equal(3, r.FilesLoaded);
    }
}
