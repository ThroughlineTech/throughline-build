using Xunit;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Scaffold.Tests;

public class OpDocParserTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "example-op-doc.md");

    // Helper: run parser on inline lines (avoids file system dependency for most tests)
    private static ParseResult Parse(string content) =>
        OpDocParser.ParseLines(content.Split('\n'));

    // ---- Happy path ----

    [Fact]
    public void Fixture_ParsesWithoutErrors()
    {
        var result = OpDocParser.Parse(FixturePath);
        Assert.NotNull(result);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Parsed);
    }

    [Fact]
    public void Fixture_OperationSlugIsExample()
    {
        var result = OpDocParser.Parse(FixturePath);
        Assert.Equal("example", result.Parsed!.OperationSlug);
    }

    [Fact]
    public void Fixture_HasTwoPlans()
    {
        var result = OpDocParser.Parse(FixturePath);
        Assert.Equal(2, result.Parsed!.Plans.Count);
    }

    [Fact]
    public void Fixture_PlanAHasThreeBriefs()
    {
        var result = OpDocParser.Parse(FixturePath);
        var planA = result.Parsed!.Plans.First(p => p.Id == "A");
        Assert.Equal(3, planA.Briefs.Count);
    }

    [Fact]
    public void Fixture_PlanBHasTwoBriefs()
    {
        var result = OpDocParser.Parse(FixturePath);
        var planB = result.Parsed!.Plans.First(p => p.Id == "B");
        Assert.Equal(2, planB.Briefs.Count);
    }

    [Fact]
    public void Fixture_DispatchOrderHasTwoEntries()
    {
        var result = OpDocParser.Parse(FixturePath);
        Assert.Equal(2, result.Parsed!.DispatchOrder.Count);
    }

    [Fact]
    public void Fixture_PlanBDependsOnA()
    {
        var result = OpDocParser.Parse(FixturePath);
        var entryB = result.Parsed!.DispatchOrder.First(e => e.PlanId == "B");
        Assert.Equal("A", entryB.DependsOn);
    }

    [Fact]
    public void Fixture_PlanADependsOnNull()
    {
        var result = OpDocParser.Parse(FixturePath);
        var entryA = result.Parsed!.DispatchOrder.First(e => e.PlanId == "A");
        Assert.Null(entryA.DependsOn);
    }

    [Fact]
    public void Fixture_PlanABrief01HasOutOfScope()
    {
        var result = OpDocParser.Parse(FixturePath);
        var planA = result.Parsed!.Plans.First(p => p.Id == "A");
        var brief01 = planA.Briefs.First(b => b.Number == 1);
        Assert.NotEmpty(brief01.OutOfScope);
    }

    [Fact]
    public void Fixture_BriefsHaveAcceptanceCriteria()
    {
        var result = OpDocParser.Parse(FixturePath);
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
        var result = OpDocParser.Parse(FixturePath);
        Assert.NotEmpty(result.Parsed!.WhatDoneLooksLike);
    }

    [Fact]
    public void Fixture_WhyIsPopulated()
    {
        var result = OpDocParser.Parse(FixturePath);
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
        var result = OpDocParser.Parse(FixturePath);
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
