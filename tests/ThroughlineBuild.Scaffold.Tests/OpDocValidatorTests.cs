using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public class OpDocValidatorTests
{
    private static Brief MakeBrief(
        string slug = "test",
        int number = 1,
        string title = "Test Brief",
        string goal = "Test goal",
        string? notes = "Test notes",
        IReadOnlyList<string>? outOfScope = null,
        IReadOnlyList<string>? acceptanceCriteria = null) =>
        new Brief(
            Slug: slug,
            Number: number,
            Title: title,
            Goal: goal,
            Inputs: new List<string> { "input1" },
            Outputs: new List<string> { "output1" },
            AcceptanceCriteria: acceptanceCriteria ?? new List<string> { "AC1" },
            Notes: notes,
            OutOfScope: outOfScope ?? new List<string> { "OOS1", "OOS2", "OOS3" },
            DependsOn: null);

    private static Plan MakePlan(string id = "A", string name = "Plan A", string goal = "Complete X", IReadOnlyList<Brief>? briefs = null) =>
        new Plan(
            Id: id,
            Name: name,
            Goal: goal,
            Briefs: briefs ?? new List<Brief> { MakeBrief() });

    private static DispatchEntry MakeDispatchEntry(string planId = "A", string name = "Plan A", string? dependsOn = null, string effort = "M") =>
        new DispatchEntry(
            PlanId: planId,
            Name: name,
            DependsOn: dependsOn,
            Effort: effort);

    private static OpDoc MakeValidOpDoc(
        string slug = "op-test",
        string title = "Test Operation",
        string why = "Test why",
        string whatDoneLooksLike = "All tests pass",
        IReadOnlyList<DispatchEntry>? dispatchOrder = null,
        IReadOnlyList<Plan>? plans = null) =>
        new OpDoc(
            OperationSlug: slug,
            Title: title,
            Why: why,
            DispatchOrder: dispatchOrder ?? new List<DispatchEntry> { MakeDispatchEntry() },
            Plans: plans ?? new List<Plan> { MakePlan() },
            WhatDoneLooksLike: whatDoneLooksLike);

    [Fact]
    public void ValidOpDoc_PassesValidationCleanly()
    {
        var opDoc = MakeValidOpDoc();
        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void OpDocMissingOutOfScope_ProducesErrorWithCorrectCodeAndPath()
    {
        var brief = MakeBrief(outOfScope: new List<string>());
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        var error = result.Errors[0];
        Assert.Equal("OOS_MISSING", error.Code);
        Assert.Equal("Plans[A].Briefs[01].OutOfScope", error.Path);
    }

    [Fact]
    public void OpDocWithForbiddenRisksSection_ProducesError()
    {
        var brief = MakeBrief(notes: "Risks: something bad might happen");
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "FORBIDDEN_SECTION");
        Assert.NotNull(error);
        Assert.Contains("Risks", error.Message);
    }

    [Fact]
    public void OpDocWithNonSequentialBriefNumbering_ProducesError()
    {
        var brief1 = MakeBrief(number: 1);
        var brief2 = MakeBrief(number: 3); // Gap: missing 2
        var plan = MakePlan(briefs: new List<Brief> { brief1, brief2 });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var gapError = result.Errors.FirstOrDefault(e => e.Code == "BRIEF_NUMBER_GAP");
        Assert.NotNull(gapError);
        Assert.Equal("Plans[A].Briefs[02]", gapError.Path);
    }

    [Fact]
    public void OpDocWithLightOutOfScopeCoverage_ProducesWarning()
    {
        var brief = MakeBrief(outOfScope: new List<string> { "OOS1" }); // Only 1 item
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid); // Not an error
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "OOS_LIGHT");
        Assert.NotNull(warning);
        Assert.Equal("Plans[A].Briefs[01].OutOfScope", warning.Path);
    }

    [Fact]
    public void AcceptanceCriterionWithImplementLanguage_ProducesWarning()
    {
        var brief = MakeBrief(acceptanceCriteria: new List<string> { "Implement the feature" });
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "ACCEPTANCE_HOW");
        Assert.NotNull(warning);
        Assert.Equal("Plans[A].Briefs[01].AcceptanceCriteria", warning.Path);
    }

    [Fact]
    public void DispatchOrderReferencingNonExistentPlan_ProducesError()
    {
        var entry = MakeDispatchEntry(planId: "Z"); // Plan Z doesn't exist
        var plan = MakePlan(id: "A");
        var opDoc = MakeValidOpDoc(
            dispatchOrder: new List<DispatchEntry> { entry },
            plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "DISPATCH_PLAN_MISSING");
        Assert.NotNull(error);
    }

    [Fact]
    public void AcceptanceCriterionWithCreateFileLanguage_ProducesWarning()
    {
        var brief = MakeBrief(acceptanceCriteria: new List<string> { "Create file foo.cs" });
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "ACCEPTANCE_HOW");
        Assert.NotNull(warning);
    }

    [Fact]
    public void AcceptanceCriterionWithWriteCodeLanguage_ProducesWarning()
    {
        var brief = MakeBrief(acceptanceCriteria: new List<string> { "Write code that handles errors" });
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "ACCEPTANCE_HOW");
        Assert.NotNull(warning);
    }

    [Fact]
    public void BriefWithEmptyNotes_ProducesWarning()
    {
        var brief = MakeBrief(notes: "");
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "NOTES_EMPTY");
        Assert.NotNull(warning);
    }

    [Fact]
    public void NonStandardEffort_ProducesWarning()
    {
        var entry = MakeDispatchEntry(effort: "XL");
        var plan = MakePlan();
        var opDoc = MakeValidOpDoc(
            dispatchOrder: new List<DispatchEntry> { entry },
            plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "EFFORT_NONSTANDARD");
        Assert.NotNull(warning);
    }

    [Fact]
    public void InvalidOperationSlug_ProducesError()
    {
        var opDoc = MakeValidOpDoc(slug: "Op-Test"); // Starts with uppercase

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "SLUG_INVALID");
        Assert.NotNull(error);
    }

    [Fact]
    public void BriefWithEmptySlug_ProducesError()
    {
        var brief = MakeBrief(slug: "");
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "SLUG_EMPTY");
        Assert.NotNull(error);
    }

    [Fact]
    public void BriefWithEmptyTitle_ProducesError()
    {
        var brief = MakeBrief(title: "");
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "TITLE_EMPTY");
        Assert.NotNull(error);
    }

    [Fact]
    public void BriefWithEmptyGoal_ProducesError()
    {
        var brief = MakeBrief(goal: "");
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "GOAL_EMPTY");
        Assert.NotNull(error);
    }

    [Fact]
    public void BriefWithoutAcceptanceCriteria_ProducesError()
    {
        var brief = MakeBrief(acceptanceCriteria: new List<string>());
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "ACCEPTANCE_MISSING");
        Assert.NotNull(error);
    }

    [Fact]
    public void PlanWithEmptyGoal_ProducesError()
    {
        var plan = MakePlan(goal: "");
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "PLAN_GOAL_EMPTY");
        Assert.NotNull(error);
    }

    [Fact]
    public void DuplicateBriefNumbers_ProducesError()
    {
        var brief1 = MakeBrief(number: 1);
        var brief2 = MakeBrief(number: 1); // Duplicate
        var plan = MakePlan(briefs: new List<Brief> { brief1, brief2 });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "BRIEF_NUMBER_DUPLICATE");
        Assert.NotNull(error);
    }

    [Fact]
    public void GloballySequentialBriefNumbering_MultiplePlans_PassesWithoutGapErrors()
    {
        // Plan A: 01-04, Plan B: 05-07 - the established globally-sequential convention
        var planA = MakePlan(id: "A", briefs: new List<Brief>
        {
            MakeBrief(number: 1), MakeBrief(number: 2), MakeBrief(number: 3), MakeBrief(number: 4)
        });
        var planB = MakePlan(id: "B", name: "Plan B", goal: "Complete Y", briefs: new List<Brief>
        {
            MakeBrief(number: 5), MakeBrief(number: 6), MakeBrief(number: 7)
        });
        var dispatchOrder = new List<DispatchEntry>
        {
            MakeDispatchEntry(planId: "A"),
            MakeDispatchEntry(planId: "B", name: "Plan B", dependsOn: "A")
        };
        var opDoc = MakeValidOpDoc(
            dispatchOrder: dispatchOrder,
            plans: new List<Plan> { planA, planB });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Code == "BRIEF_NUMBER_GAP");
        Assert.DoesNotContain(result.Warnings, w => w.Code == "BRIEF_PLAN_GAP");
    }

    [Fact]
    public void CrossPlanBriefNumberingGap_ProducesWarning()
    {
        // Plan A ends at 4, Plan B starts at 6 - skips 5
        var planA = MakePlan(id: "A", briefs: new List<Brief>
        {
            MakeBrief(number: 1), MakeBrief(number: 2), MakeBrief(number: 3), MakeBrief(number: 4)
        });
        var planB = MakePlan(id: "B", name: "Plan B", goal: "Complete Y", briefs: new List<Brief>
        {
            MakeBrief(number: 6), MakeBrief(number: 7)
        });
        var dispatchOrder = new List<DispatchEntry>
        {
            MakeDispatchEntry(planId: "A"),
            MakeDispatchEntry(planId: "B", name: "Plan B", dependsOn: "A")
        };
        var opDoc = MakeValidOpDoc(
            dispatchOrder: dispatchOrder,
            plans: new List<Plan> { planA, planB });

        var result = OpDocValidator.Validate(opDoc);

        var warning = result.Warnings.FirstOrDefault(w => w.Code == "BRIEF_PLAN_GAP");
        Assert.NotNull(warning);
        Assert.Equal("Plans[B].Briefs[06]", warning.Path);
        Assert.Contains("05", warning.Message);
    }

    [Fact]
    public void InvalidDependsOnReference_ProducesError()
    {
        var entry = MakeDispatchEntry(planId: "A", dependsOn: "Z"); // Z doesn't exist
        var plan = MakePlan(id: "A");
        var opDoc = MakeValidOpDoc(
            dispatchOrder: new List<DispatchEntry> { entry },
            plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var error = result.Errors.FirstOrDefault(e => e.Code == "DISPATCH_DEP_INVALID");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidDependsOnWithDash_PassesValidation()
    {
        var entry = MakeDispatchEntry(planId: "A", dependsOn: "-");
        var plan = MakePlan(id: "A");
        var opDoc = MakeValidOpDoc(
            dispatchOrder: new List<DispatchEntry> { entry },
            plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CaseInsensitiveHowDetection_DetectsImplement()
    {
        var brief = MakeBrief(acceptanceCriteria: new List<string> { "Implement the parser" });
        var plan = MakePlan(briefs: new List<Brief> { brief });
        var opDoc = MakeValidOpDoc(plans: new List<Plan> { plan });

        var result = OpDocValidator.Validate(opDoc);

        Assert.True(result.IsValid);
        var warning = result.Warnings.FirstOrDefault(w => w.Code == "ACCEPTANCE_HOW");
        Assert.NotNull(warning);
    }
}
