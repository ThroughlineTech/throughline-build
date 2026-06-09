using System.Collections.Generic;
using System.Linq;
using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

/// <summary>
/// Stack-free facts for the pre-load mechanism. No disk: the reader is a fake. The extraction keys on
/// path SHAPE (a separator + a dotted final segment), never on language, so the second-stack tests
/// (.py / .go) are the primary proof no TypeScript assumption leaked.
/// </summary>
public class PreloadedContextBuilderTests
{
    private static Func<string, string?> Reader(Dictionary<string, string> files) =>
        path => files.TryGetValue(path, out var c) ? c : null;

    private static ProjectContext Project(params string[] conventionFiles) =>
        ProjectContext.Empty with { ConventionFiles = conventionFiles };

    // Mirrors BriefHtmlRenderer.RenderBrief output: <h3>Inputs</h3><ul><li><p>...<code>x</code>...</p></li>...</ul>
    private static string InputsHtml(params string[] bulletsInnerHtml)
    {
        var lis = string.Concat(bulletsInnerHtml.Select(b => $"<li><p>{b}</p></li>"));
        return $"<h3>Goal</h3><p>g</p><h3>Inputs</h3><ul>{lis}</ul>";
    }

    [Fact]
    public void Extract_KeepsPaths_RejectsSymbolsAndRoutes()
    {
        var html = InputsHtml(
            "From B02 <code>src/data/types.ts</code>: <code>Survey</code>, <code>getSurvey(id)</code>.",
            "React Router <code>useParams</code> and route <code>/responses/:responseId</code>.");

        var paths = PreloadedContextBuilder.ExtractNamedInputPaths(html);

        Assert.Equal(new[] { "src/data/types.ts" }, paths);
    }

    [Fact]
    public void Extract_OnlyScansTheInputsSection_NotOutputs()
    {
        var html = "<h3>Inputs</h3><ul><li><p><code>src/in.ts</code></p></li></ul>"
                 + "<h3>Outputs</h3><ul><li><p><code>src/out.ts</code></p></li></ul>";

        var paths = PreloadedContextBuilder.ExtractNamedInputPaths(html);

        Assert.Equal(new[] { "src/in.ts" }, paths);
    }

    [Fact]
    public void Extract_NoInputsSection_ReturnsEmpty()
    {
        Assert.Empty(PreloadedContextBuilder.ExtractNamedInputPaths("<h3>Goal</h3><p>g</p>"));
        Assert.Empty(PreloadedContextBuilder.ExtractNamedInputPaths(null));
    }

    [Fact]
    public void Extract_SecondStack_PythonAndGoPaths()
    {
        var html = InputsHtml(
            "From the model layer <code>src/app/models.py</code> and <code>internal/store/repo.go</code>; "
          + "the symbol <code>NewStore</code> is not a path.");

        var paths = PreloadedContextBuilder.ExtractNamedInputPaths(html);

        Assert.Equal(new[] { "src/app/models.py", "internal/store/repo.go" }, paths);
    }

    [Fact]
    public void Build_InlinesNamedInput_AndMarksMissing()
    {
        var html = InputsHtml(
            "From B02 <code>src/data/types.ts</code> and <code>src/data/repository.ts</code>.");
        var files = new Dictionary<string, string> { ["src/data/types.ts"] = "export type Survey = {};" };

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files));

        Assert.Contains("## Pre-loaded context", section);
        Assert.Contains("`src/data/types.ts`", section);
        Assert.Contains("export type Survey = {};", section);
        Assert.Contains("`src/data/repository.ts` (not found)", section);
        Assert.StartsWith("\n", section); // leading newline so the template stays inert when empty
    }

    [Fact]
    public void Build_ConventionFilesFirst_ThenNamedInputs_Deduped()
    {
        var html = InputsHtml("<code>src/data/types.ts</code> and <code>src/data/repository.ts</code>.");
        var project = Project("src/setupTests.ts", "src/data/types.ts"); // types.ts also a named input
        var files = new Dictionary<string, string>
        {
            ["src/setupTests.ts"] = "setup();",
            ["src/data/types.ts"] = "types;",
            ["src/data/repository.ts"] = "repo;",
        };

        var section = PreloadedContextBuilder.Build(html, project, Reader(files));

        // Order: setupTests (convention) -> types.ts (convention, NOT repeated as named) -> repository.ts (named)
        int setup = section.IndexOf("`src/setupTests.ts`");
        int types = section.IndexOf("`src/data/types.ts`");
        int repo = section.IndexOf("`src/data/repository.ts`");
        Assert.True(setup >= 0 && types > setup && repo > types, $"order setup={setup} types={types} repo={repo}");
        // types.ts appears exactly once despite being both a convention file and a named input
        Assert.Equal(types, section.LastIndexOf("`src/data/types.ts`"));
    }

    [Fact]
    public void Build_NoConventionAndNoNamedInputs_ReturnsEmpty()
    {
        var html = InputsHtml("only symbols here: <code>Survey</code>, <code>Answer</code>.");
        Assert.Equal(string.Empty, PreloadedContextBuilder.Build(html, ProjectContext.Empty, _ => null));
        Assert.Equal(string.Empty, PreloadedContextBuilder.Build(null, ProjectContext.Empty, _ => null));
    }

    [Fact]
    public void Build_HtmlEscapedToken_IsUnescapedBeforeMatching()
    {
        // A path is plain, but the bullet around it is escaped (&amp;); the symbol Record<...> is
        // rendered escaped and must not be mistaken for a path.
        var html = InputsHtml("config &amp; setup: <code>src/a&amp;b/types.ts</code>, type <code>Record&lt;string, Answer&gt;</code>.");
        var files = new Dictionary<string, string> { ["src/a&b/types.ts"] = "x;" };

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files));

        Assert.Contains("`src/a&b/types.ts`", section);
        Assert.Contains("x;", section);
        Assert.DoesNotContain("Record", section);
    }

    [Fact]
    public void Build_OversizedFile_TruncatedWithMarker()
    {
        var html = InputsHtml("<code>src/big.ts</code>");
        var big = new string('x', 5000);
        var files = new Dictionary<string, string> { ["src/big.ts"] = big };
        var opts = new PreloadOptions(MaxFiles: 12, MaxCharsPerFile: 1000, MaxTotalChars: 64 * 1024);

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("truncated", section);
        Assert.True(section.Length < 5000, "the 5000-char file must not be inlined whole");
    }

    [Fact]
    public void Build_TotalBudgetExceeded_RemainingFilesOmittedNotSilent()
    {
        var html = InputsHtml("<code>src/a.ts</code>, <code>src/b.ts</code>");
        var files = new Dictionary<string, string>
        {
            ["src/a.ts"] = new string('a', 900),
            ["src/b.ts"] = new string('b', 900),
        };
        var opts = new PreloadOptions(MaxFiles: 12, MaxCharsPerFile: 16 * 1024, MaxTotalChars: 1000);

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("`src/a.ts`", section);
        Assert.Contains("omitted (pre-load cap)", section);
        Assert.Contains("`src/b.ts`", section);
        Assert.DoesNotContain(new string('b', 900), section);
    }

    [Fact]
    public void Build_FileCountCap_OverflowListed()
    {
        var html = InputsHtml("<code>src/a.ts</code>, <code>src/b.ts</code>, <code>src/c.ts</code>");
        var files = new Dictionary<string, string>
        {
            ["src/a.ts"] = "a;",
            ["src/b.ts"] = "b;",
            ["src/c.ts"] = "c;",
        };
        var opts = new PreloadOptions(MaxFiles: 1, MaxCharsPerFile: 16 * 1024, MaxTotalChars: 64 * 1024);

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, Reader(files), opts);

        Assert.Contains("a;", section);
        Assert.Contains("omitted (pre-load cap)", section);
        Assert.DoesNotContain("b;", section);
        Assert.DoesNotContain("c;", section);
    }

    [Fact]
    public void Build_ParentEscapeAndRootedPaths_NeverReachReaderAndAreNotInlined()
    {
        var html = InputsHtml(
            "evil <code>../secret.ts</code>, rooted <code>/etc/passwd.txt</code>, ok <code>src/ok.ts</code>");
        var asked = new List<string>();
        Func<string, string?> reader = p => { asked.Add(p); return p == "src/ok.ts" ? "ok;" : null; };

        var section = PreloadedContextBuilder.Build(html, ProjectContext.Empty, reader);

        Assert.Contains("src/ok.ts", section);
        Assert.DoesNotContain("secret", section);
        Assert.DoesNotContain("passwd", section);
        // The escaping/rooted paths are rejected at the path-validity gate and never reach the reader.
        Assert.Equal(new[] { "src/ok.ts" }, asked);
    }

    [Fact]
    public void Build_ConventionFileContent_Inlined_NoLanguageBranch_SecondStack()
    {
        // A python-shaped convention bundle proves the engine carries whatever the deriver chose.
        var project = Project("conftest.py", "tests/test_example.py");
        var html = InputsHtml("<code>src/app/models.py</code>");
        var files = new Dictionary<string, string>
        {
            ["conftest.py"] = "import pytest",
            ["tests/test_example.py"] = "def test_x(): assert True",
            ["src/app/models.py"] = "class Model: pass",
        };

        var section = PreloadedContextBuilder.Build(html, project, Reader(files));

        int conftest = section.IndexOf("`conftest.py`");
        int example = section.IndexOf("`tests/test_example.py`");
        int models = section.IndexOf("`src/app/models.py`");
        Assert.True(conftest >= 0 && example > conftest && models > example);
        Assert.Contains("import pytest", section);
        Assert.Contains("class Model: pass", section);
    }
}
