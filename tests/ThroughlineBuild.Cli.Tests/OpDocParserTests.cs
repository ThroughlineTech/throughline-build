using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class OpDocParserTests
{
    // Minimal valid op-doc with one brief. OOS section is substituted per test.
    private static string[] MinimalOpDoc(string oosSection) =>
        $"""
        # Operation: test-op

        Test operation title.

        ## Why this exists

        Why body.

        ## Dispatch order

        | Plan | Name | Depends on | Effort |
        |------|------|------------|--------|
        | A | Test Plan | - | S |

        ## Plan A: Test Plan

        ### Goal

        Plan goal.

        ### Briefs

        | # | Slug | Intent | Deps | Files |
        |---|------|--------|------|-------|
        | 01 | test-brief | Test brief intent | - | file.txt |

        ### Briefs - detail

        #### Brief 01: test-brief

        Goal: Test goal text.

        Acceptance:
        - [ ] Test criterion

        {oosSection}

        ## What done looks like

        Done description.
        """.Split('\n');

    [Fact]
    public void OosProse_ParsedWithoutError()
    {
        var lines = MinimalOpDoc("OOS: Do not implement X or change Y.");

        var result = OpDocParser.ParseLines(lines);

        var oosErrors = result.Errors.Where(e => e.Message.Contains("OOS")).ToList();
        Assert.Empty(oosErrors);

        var brief = result.Parsed?.Plans[0].Briefs[0];
        Assert.NotNull(brief);
        Assert.Single(brief.OutOfScope);
        Assert.Equal("Do not implement X or change Y.", brief.OutOfScope[0]);
    }

    [Fact]
    public void OosBullets_ParsedWithoutError()
    {
        var lines = MinimalOpDoc("OOS:\n        - Do not implement X\n        - Do not change Y");

        var result = OpDocParser.ParseLines(lines);

        var oosErrors = result.Errors.Where(e => e.Message.Contains("OOS")).ToList();
        Assert.Empty(oosErrors);

        var brief = result.Parsed?.Plans[0].Briefs[0];
        Assert.NotNull(brief);
        Assert.Equal(2, brief.OutOfScope.Count);
    }

    [Fact]
    public void OosMissing_ProducesError()
    {
        var lines = MinimalOpDoc(string.Empty);

        var result = OpDocParser.ParseLines(lines);

        Assert.Contains(result.Errors,
            e => e.Message.Contains("brief_subsection_missing") && e.Message.Contains("OOS"));
    }
}
