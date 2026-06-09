using Xunit;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Scaffold.Tests;

public class OpDocParserTests
{
    static OpDocParserTests()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    }

    // Helper: run parser on inline lines (avoids file system dependency for most tests)
    private static ParseResult Parse(string content) =>
        OpDocParser.ParseLines(content.Split('\n'));

    private static ParseResult ParseFixture() =>
        Parse(OpDocDocsLoader.LoadExample());

    // ---- Happy path ----

    [Fact]
    public void Fixture_ParsesWithoutErrors()
    {
        var result = ParseFixture();
        Assert.NotNull(result);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Parsed);
    }

    [Fact]
    public void Fixture_OperationSlugIsExample()
    {
        var result = ParseFixture();
        Assert.Equal("cli-build-version-embedding", result.Parsed!.OperationSlug);
    }

    [Fact]
    public void Fixture_HasTwoPlans()
    {
        var result = ParseFixture();
        Assert.Equal(2, result.Parsed!.Plans.Count);
    }

    [Fact]
    public void Fixture_PlanAHasThreeBriefs()
    {
        var result = ParseFixture();
        var planA = result.Parsed!.Plans.First(p => p.Id == "A");
        Assert.Equal(3, planA.Briefs.Count);
    }

    [Fact]
    public void Fixture_PlanBHasTwoBriefs()
    {
        var result = ParseFixture();
        var planB = result.Parsed!.Plans.First(p => p.Id == "B");
        Assert.Equal(2, planB.Briefs.Count);
    }

    [Fact]
    public void Fixture_DispatchOrderHasTwoEntries()
    {
        var result = ParseFixture();
        Assert.Equal(2, result.Parsed!.DispatchOrder.Count);
    }

    [Fact]
    public void Fixture_PlanBDependsOnA()
    {
        var result = ParseFixture();
        var entryB = result.Parsed!.DispatchOrder.First(e => e.PlanId == "B");
        Assert.Equal("A", entryB.DependsOn);
    }

    [Fact]
    public void Fixture_PlanADependsOnNull()
    {
        var result = ParseFixture();
        var entryA = result.Parsed!.DispatchOrder.First(e => e.PlanId == "A");
        Assert.Null(entryA.DependsOn);
    }

    [Fact]
    public void Fixture_PlanABrief01HasOutOfScope()
    {
        var result = ParseFixture();
        var planA = result.Parsed!.Plans.First(p => p.Id == "A");
        var brief01 = planA.Briefs.First(b => b.Number == 1);
        Assert.NotEmpty(brief01.OutOfScope);
    }

    [Fact]
    public void Fixture_BriefsHaveAcceptanceCriteria()
    {
        var result = ParseFixture();
        foreach (var plan in result.Parsed!.Plans)
        {
            foreach (var brief in plan.Briefs)
            {
                Assert.NotEmpty(brief.AcceptanceCriteria);
            }
        }
    }

    [Fact]
    public void Fixture_WhatDoneLooksLikeIsPopulated()
    {
        var result = ParseFixture();
        Assert.NotEmpty(result.Parsed!.WhatDoneLooksLike);
    }

    [Fact]
    public void Fixture_WhyIsPopulated()
    {
        var result = ParseFixture();
        Assert.NotEmpty(result.Parsed!.Why);
    }

    // ---- Missing OOS in one brief ----

    [Fact]
    public void MissingOOS_ProducesExactlyOneError_PointingAtBrief()
    {
        // Build a minimal valid doc, drop OOS from Plan A Brief 02
        string content = BuildMinimalDoc(dropOosFromPlanA: 2);
        var result = Parse(content);

        // Should have exactly one OOS error
        var oosErrors = result.Errors
            .Where(e => e.Message.Contains("OOS") || e.Message.Contains("oos"))
            .ToList();
        Assert.Single(oosErrors);

        var err = oosErrors[0];
        Assert.Contains("Plans[A].Briefs[02]", err.Section);
        Assert.Contains("brief_subsection_missing", err.Message);
    }

    // ---- Extra unknown plan (warning, not error) ----

    [Fact]
    public void ExtraUnknownPlan_ProducesWarningNotBlockingError()
    {
        // Add ## Plan C: Stray not in dispatch order
        string content = BuildMinimalDoc() + "\n\n## Plan C: Stray\n\n### Goal\n\nStray goal.\n\n### Briefs\n\n| # | Slug | Intent | Deps | Files |\n|---|------|--------|------|-------|\n| 01 | stray-brief | Stray intent | - | - |\n\n### Briefs - detail\n\n#### Brief 01: stray-brief\n\nGoal: Stray brief goal.\n\nInputs:\n- stray input\n\nOutputs:\n- stray output\n\nAcceptance:\n- [ ] stray acceptance\n\nOOS:\n- stray OOS\n";
        var result = Parse(content);

        // No hard errors from the warning
        var hardErrors = result.Errors
            .Where(e => !e.Message.StartsWith("warning:"))
            .ToList();
        Assert.Empty(hardErrors);

        // Should have exactly one warning about Plan C not in dispatch
        var warnings = result.Errors
            .Where(e => e.Message.StartsWith("warning:"))
            .ToList();
        Assert.Single(warnings);
        Assert.Contains("Plan C", warnings[0].Message);
    }

    // ---- Malformed dispatch table ----

    [Fact]
    public void MalformedDispatchTable_MissingEffortColumn_ProducesError()
    {
        string content = BuildMinimalDoc(dropEffortColumn: true);
        var result = Parse(content);

        var dispatchErrors = result.Errors
            .Where(e => e.Message.Contains("dispatch_columns_missing") || e.Message.Contains("effort"))
            .ToList();
        Assert.NotEmpty(dispatchErrors);
    }

    [Fact]
    public void MalformedDispatchTable_RowColCountMismatch_ProducesError()
    {
        // Row with wrong number of cells
        string content =
            "# Operation: test-op\n\nTest title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M | extra-cell |\n\n" +
            BuildPlanASection() +
            "\n\n## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        var rowErrors = result.Errors
            .Where(e => e.Message.Contains("dispatch_row_malformed"))
            .ToList();
        Assert.NotEmpty(rowErrors);
    }

    // ---- Missing H1 ----

    [Fact]
    public void MissingH1_ParsedIsNull_MissingH1Error()
    {
        string content = "## Why this exists\n\nSome why.\n\n## What done looks like\n\nDone.\n";
        var result = Parse(content);

        Assert.Null(result.Parsed);
        Assert.Single(result.Errors);
        Assert.Contains("missing_h1", result.Errors[0].Message);
    }

    // ---- Bullet marker tolerance (* and -) ----

    [Fact]
    public void StarBullets_ParsedAsInputsOutputsOOS()
    {
        string content =
            "# Operation: star-op\n\nStar op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with star bullets\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | star-brief | Star brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: star-brief\n\n" +
            "Goal: Star brief goal.\n\n" +
            "Inputs:\n* star input one\n* star input two\n\n" +
            "Outputs:\n* star output\n\n" +
            "Acceptance:\n- [ ] star acceptance\n\n" +
            "OOS:\n* star oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Equal(2, brief.Inputs.Count);
        Assert.Single(brief.Outputs);
        Assert.Single(brief.OutOfScope);
    }

    // ---- Bold-markdown label tolerance (**Goal:** etc.) ----

    [Fact]
    public void BoldLabels_ParsedAsGoalAcceptanceOOS()
    {
        // Mirrors the natural LLM output that broke scaffolding: subsection labels
        // wrapped in markdown bold (**Goal:**), with prose Goal, checkbox Acceptance,
        // and bullet OOS. All three required subsections must parse cleanly.
        string content =
            "# Operation: bold-op\n\nBold op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with bold labels\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | bold-brief | Bold brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: bold-brief\n\n" +
            "**Goal:** Bold brief goal as prose.\n\n" +
            "**Inputs:** Some prose inputs.\n\n" +
            "**Outputs:** Prose outputs.\n\n" +
            "**Acceptance:**\n- [ ] bold acceptance criterion\n\n" +
            "**Notes:** Some notes.\n\n" +
            "**OOS:**\n- bold oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Equal("Bold brief goal as prose.", brief.Goal);
        Assert.Single(brief.AcceptanceCriteria);
        Assert.Single(brief.OutOfScope);
    }

    [Fact]
    public void BoldLabels_ColonOutsideEmphasis_AlsoParsed()
    {
        // "**Goal**:" (colon outside the bold) is the other emphasis form a model emits.
        string content =
            "# Operation: bold-op2\n\nBold op2 title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with bold-outside labels\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | bold-brief | Bold brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: bold-brief\n\n" +
            "**Goal**: Goal with colon outside.\n\n" +
            "**Acceptance**:\n- [ ] criterion\n\n" +
            "**OOS**:\n- oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Equal("Goal with colon outside.", brief.Goal);
    }

    [Fact]
    public void PlainLabel_WithBoldContent_PreservesContentEmphasis()
    {
        // Non-regression: a plain "Goal:" whose content starts with bold must keep
        // the leading "**" as content, not mistake it for a closing label marker.
        string content =
            "# Operation: content-op\n\nContent op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with bold content\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | content-brief | Content brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: content-brief\n\n" +
            "Goal: **critical** thing to do.\n\n" +
            "Acceptance:\n- [ ] criterion\n\n" +
            "OOS:\n- oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Equal("**critical** thing to do.", brief.Goal);
    }

    // ---- Preload label (experiment 3: positive-only context pre-load channel) ----

    [Fact]
    public void PreloadLabel_ParsesToPreloadFiles_IndependentOfInputs()
    {
        // A `Preload:` block is a bullet list of file PATHS. It parses to Brief.PreloadFiles and is
        // independent of the prose Inputs read-map (both may coexist).
        string content =
            "# Operation: preload-op\n\nPreload op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with a preload block\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | preload-brief | Preload brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: preload-brief\n\n" +
            "Goal: Brief goal.\n\n" +
            "Inputs:\n- src/data/types.ts\n\n" +
            "Preload:\n- src/data/aggregate.ts\n- src/pages/admin/AdminResults.tsx\n\n" +
            "Acceptance:\n- [ ] criterion\n\n" +
            "OOS:\n- oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Equal(new[] { "src/data/aggregate.ts", "src/pages/admin/AdminResults.tsx" }, brief.PreloadFiles);
        Assert.Equal(new[] { "src/data/types.ts" }, brief.Inputs);
    }

    [Fact]
    public void NoPreloadBlock_PreloadFilesEmpty()
    {
        // A brief with no Preload block leaves PreloadFiles empty (it is optional, not required).
        string content =
            "# Operation: nopre-op\n\nNo-preload op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan without a preload block\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | plain-brief | Plain brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: plain-brief\n\n" +
            "Goal: Brief goal.\n\n" +
            "Acceptance:\n- [ ] criterion\n\n" +
            "OOS:\n- oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Parsed!.Plans[0].Briefs[0].PreloadFiles);
    }

    [Fact]
    public void ParentheticalInputsLabel_AbsorbedIntoGoal_NotAnInputsSection()
    {
        // The `Inputs (parenthetical):` form does NOT match the label regex, so its content is
        // absorbed into the preceding Goal - the exact reason experiment 3 adds a dedicated Preload
        // label rather than scraping the prose read-map. Adding Preload to the regex must NOT change
        // this behavior (out of scope per the plan: do not fix the parenthetical absorption).
        string content =
            "# Operation: paren-op\n\nParen op title.\n\n" +
            "## Why this exists\n\nWhy content.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            "## Plan A: Plan with a parenthetical inputs label\n\n" +
            "### Goal\n\nPlan goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | paren-brief | Paren brief | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            "#### Brief 01: paren-brief\n\n" +
            "Goal: Brief goal.\n\n" +
            "Inputs (read these; do not rediscover them):\n- src/data/types.ts\n\n" +
            "Acceptance:\n- [ ] criterion\n\n" +
            "OOS:\n- oos item\n\n" +
            "## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        Assert.Empty(result.Errors);
        var brief = result.Parsed!.Plans[0].Briefs[0];
        Assert.Empty(brief.Inputs);                       // the parenthetical label never became an Inputs section
        Assert.Empty(brief.PreloadFiles);                 // and no Preload block was declared
        Assert.Contains("src/data/types.ts", brief.Goal); // the read-map prose absorbed into Goal
    }

    // ---- Missing required H2 sections ----

    [Fact]
    public void MissingWhySection_ProducesError()
    {
        string content =
            "# Operation: no-why\n\nNo why title.\n\n" +
            "## Dispatch order\n\n" +
            "| Plan | Name | Depends on | Effort |\n" +
            "| ---- | ---- | ---------- | ------ |\n" +
            "| A    | Plan A | - | M |\n\n" +
            BuildPlanASection() +
            "\n\n## What done looks like\n\nAll done.\n";

        var result = Parse(content);
        var sectionErrors = result.Errors
            .Where(e => e.Message.Contains("missing_section") && e.Message.Contains("Why"))
            .ToList();
        Assert.Single(sectionErrors);
    }

    // ---- Multiple plan sections, multiple briefs ----

    [Fact]
    public void TwoPlansWithMultipleBriefs_AllPopulated()
    {
        var result = ParseFixture();
        Assert.NotNull(result.Parsed);
        int totalBriefs = result.Parsed!.Plans.Sum(p => p.Briefs.Count);
        Assert.Equal(5, totalBriefs);
    }

    // ---- Helpers for building minimal inline op-docs ----

    private static string BuildMinimalDoc(int dropOosFromPlanA = -1, bool dropEffortColumn = false)
    {
        string dispatchHeader = dropEffortColumn
            ? "| Plan | Name | Depends on |\n| ---- | ---- | ---------- |"
            : "| Plan | Name | Depends on | Effort |\n| ---- | ---- | ---------- | ------ |";

        string dispatchRows = dropEffortColumn
            ? "| A    | Plan A | - |\n| B    | Plan B | A |"
            : "| A    | Plan A | - | M |\n| B    | Plan B | A | S |";

        string planA = BuildPlanASection(dropOosFromBrief: dropOosFromPlanA);
        string planB = BuildPlanBSection();

        return
            "# Operation: minimal-op\n\n" +
            "Minimal op title.\n\n" +
            "## Why this exists\n\n" +
            "Why content.\n\n" +
            "## Dispatch order\n\n" +
            dispatchHeader + "\n" +
            dispatchRows + "\n\n" +
            planA + "\n\n" +
            planB + "\n\n" +
            "## What done looks like\n\n" +
            "All done.\n";
    }

    private static string BuildPlanASection(int dropOosFromBrief = -1)
    {
        string brief01 = BuildBriefDetail(1, "brief-01", includeOos: dropOosFromBrief != 1);
        string brief02 = BuildBriefDetail(2, "brief-02", includeOos: dropOosFromBrief != 2);

        return
            "## Plan A: Plan A name\n\n" +
            "### Goal\n\n" +
            "Plan A goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | brief-01 | Brief 01 intent | - | - |\n" +
            "| 02 | brief-02 | Brief 02 intent | 01 | - |\n\n" +
            "### Briefs - detail\n\n" +
            brief01 + "\n\n" +
            brief02;
    }

    private static string BuildPlanBSection()
    {
        return
            "## Plan B: Plan B name\n\n" +
            "### Goal\n\n" +
            "Plan B goal.\n\n" +
            "### Briefs\n\n" +
            "| # | Slug | Intent | Deps | Files |\n" +
            "|---|------|--------|------|-------|\n" +
            "| 01 | b-brief-01 | B Brief 01 intent | - | - |\n\n" +
            "### Briefs - detail\n\n" +
            BuildBriefDetail(1, "b-brief-01", includeOos: true);
    }

    private static string BuildBriefDetail(int num, string slug, bool includeOos = true)
    {
        string numStr = num.ToString("D2");
        string oos = includeOos
            ? "OOS:\n- Do not do the other thing\n"
            : string.Empty;

        return
            $"#### Brief {numStr}: {slug}\n\n" +
            $"Goal: {slug} goal.\n\n" +
            "Inputs:\n- input one\n- input two\n\n" +
            "Outputs:\n- output one\n\n" +
            "Acceptance:\n- [ ] acceptance criterion one\n\n" +
            oos;
    }
}
