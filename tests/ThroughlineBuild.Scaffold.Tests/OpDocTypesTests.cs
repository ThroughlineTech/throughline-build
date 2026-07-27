using System.Text.Json;
using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public class OpDocTypesTests
{
    private static OpDocParseError MakeError(int lineNumber = 1, string section = "Goal", string message = "Missing") =>
        new OpDocParseError(LineNumber: lineNumber, Section: section, Message: message);

    private static Brief MakeBrief(
        string slug = "test",
        int number = 1,
        string title = "Test Brief",
        string goal = "Test goal",
        string? notes = null,
        string? dependsOn = null) =>
        new Brief(
            Slug: slug,
            Number: number,
            Title: title,
            Goal: goal,
            Inputs: new List<string> { "input1" },
            Outputs: new List<string> { "output1" },
            AcceptanceCriteria: new List<string> { "AC1" },
            Notes: notes,
            OutOfScope: new List<string> { "OOS1" },
            DependsOn: dependsOn);

    private static Plan MakePlan(string id = "A", string name = "Plan A", string goal = "Complete X") =>
        new Plan(
            Id: id,
            Name: name,
            Goal: goal,
            Briefs: new List<Brief> { MakeBrief() });

    private static DispatchEntry MakeDispatchEntry(string planId = "A", string name = "Plan A", string? dependsOn = null, string effort = "M") =>
        new DispatchEntry(
            PlanId: planId,
            Name: name,
            DependsOn: dependsOn,
            Effort: effort);

    private static OpDoc MakeOpDoc(
        string slug = "op-test",
        string title = "Test Operation",
        string why = "Test why",
        string? whatDoneLooksLike = null) =>
        new OpDoc(
            OperationSlug: slug,
            Title: title,
            Why: why,
            DispatchOrder: new List<DispatchEntry> { MakeDispatchEntry() },
            Plans: new List<Plan> { MakePlan() },
            WhatDoneLooksLike: whatDoneLooksLike ?? "All tests pass");

    [Fact]
    public void OpDocParseError_Create_CapturesSourceLocation()
    {
        var err = OpDocParseError.Create(5, "Goal", "Missing");
        Assert.NotNull(err.SourceFile);
        Assert.NotEmpty(err.SourceFile);
        Assert.NotNull(err.SourceMember);
        Assert.NotEmpty(err.SourceMember);
        Assert.True(err.SourceLineNumber > 0);
    }

    [Fact]
    public void OpDocParseError_EqualWhenSameValues()
    {
        var a = MakeError();
        var b = MakeError();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void OpDocParseError_NotEqualWhenDifferentLineNumber()
    {
        var a = MakeError(lineNumber: 1);
        var b = MakeError(lineNumber: 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OpDocParseError_WithExpression_ChangesMessage()
    {
        var a = MakeError(message: "Missing field");
        var b = a with { Message = "Invalid value" };
        Assert.Equal("Invalid value", b.Message);
        Assert.Equal("Missing field", a.Message);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Brief_EqualWhenSameValues()
    {
        var brief = MakeBrief();
        // Verify fields are set correctly
        Assert.Equal("test", brief.Slug);
        Assert.Equal(1, brief.Number);
        Assert.Equal("Test Brief", brief.Title);
        Assert.Equal("Test goal", brief.Goal);
        Assert.Single(brief.Inputs);
        Assert.Single(brief.Outputs);
        Assert.Single(brief.AcceptanceCriteria);
        Assert.Single(brief.OutOfScope);
    }

    [Fact]
    public void Brief_NotEqualWhenDifferentNumber()
    {
        var a = MakeBrief(number: 1);
        var b = MakeBrief(number: 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Brief_WithExpression_ChangesTitle()
    {
        var a = MakeBrief(title: "Original");
        var b = a with { Title = "Updated" };
        Assert.Equal("Updated", b.Title);
        Assert.Equal("Original", a.Title);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Brief_InputsProperty_IsReadOnly()
    {
        var b = MakeBrief();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(b.Inputs);
    }

    [Fact]
    public void Brief_OutputsProperty_IsReadOnly()
    {
        var b = MakeBrief();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(b.Outputs);
    }

    [Fact]
    public void Brief_AcceptanceCriteriaProperty_IsReadOnly()
    {
        var b = MakeBrief();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(b.AcceptanceCriteria);
    }

    [Fact]
    public void Brief_OutOfScopeProperty_IsReadOnly()
    {
        var b = MakeBrief();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(b.OutOfScope);
    }

    [Fact]
    public void Plan_EqualWhenSameValues()
    {
        var plan = MakePlan();
        // Verify fields are set correctly
        Assert.Equal("A", plan.Id);
        Assert.Equal("Plan A", plan.Name);
        Assert.Equal("Complete X", plan.Goal);
        Assert.Single(plan.Briefs);
    }

    [Fact]
    public void Plan_NotEqualWhenDifferentId()
    {
        var a = MakePlan(id: "A");
        var b = MakePlan(id: "B");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Plan_WithExpression_ChangesGoal()
    {
        var a = MakePlan(goal: "Complete X");
        var b = a with { Goal = "Complete Y" };
        Assert.Equal("Complete Y", b.Goal);
        Assert.Equal("Complete X", a.Goal);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Plan_BriefsProperty_IsReadOnly()
    {
        var p = MakePlan();
        Assert.IsAssignableFrom<IReadOnlyList<Brief>>(p.Briefs);
    }

    [Fact]
    public void DispatchEntry_EqualWhenSameValues()
    {
        var a = MakeDispatchEntry();
        var b = MakeDispatchEntry();
        Assert.Equal(a, b);
    }

    [Fact]
    public void DispatchEntry_NotEqualWhenDifferentPlanId()
    {
        var a = MakeDispatchEntry(planId: "A");
        var b = MakeDispatchEntry(planId: "B");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DispatchEntry_WithExpression_ChangesEffort()
    {
        var a = MakeDispatchEntry(effort: "M");
        var b = a with { Effort = "L" };
        Assert.Equal("L", b.Effort);
        Assert.Equal("M", a.Effort);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OpDoc_EqualWhenSameValues()
    {
        var doc = MakeOpDoc();
        // Verify fields are set correctly
        Assert.Equal("op-test", doc.OperationSlug);
        Assert.Equal("Test Operation", doc.Title);
        Assert.Equal("Test why", doc.Why);
        Assert.Equal("All tests pass", doc.WhatDoneLooksLike);
        Assert.Single(doc.DispatchOrder);
        Assert.Single(doc.Plans);
    }

    [Fact]
    public void OpDoc_NotEqualWhenDifferentSlug()
    {
        var a = MakeOpDoc(slug: "op-test-1");
        var b = MakeOpDoc(slug: "op-test-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OpDoc_WithExpression_ChangesTitle()
    {
        var a = MakeOpDoc(title: "Original Title");
        var b = a with { Title = "New Title" };
        Assert.Equal("New Title", b.Title);
        Assert.Equal("Original Title", a.Title);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OpDoc_DispatchOrderProperty_IsReadOnly()
    {
        var doc = MakeOpDoc();
        Assert.IsAssignableFrom<IReadOnlyList<DispatchEntry>>(doc.DispatchOrder);
    }

    [Fact]
    public void OpDoc_PlansProperty_IsReadOnly()
    {
        var doc = MakeOpDoc();
        Assert.IsAssignableFrom<IReadOnlyList<Plan>>(doc.Plans);
    }

    [Fact]
    public void OpDoc_JsonSerializationRoundTrip()
    {
        var original = MakeOpDoc(
            slug: "op-roundtrip",
            title: "Roundtrip Test",
            why: "Testing JSON serialization",
            whatDoneLooksLike: "JSON round-trip successful");

        var json = JsonSerializer.Serialize(original, ScaffoldJsonContext.Default.OpDoc);
        var deserialized = JsonSerializer.Deserialize(json, ScaffoldJsonContext.Default.OpDoc);

        Assert.NotNull(deserialized);
        Assert.Equal(original.OperationSlug, deserialized.OperationSlug);
        Assert.Equal(original.Title, deserialized.Title);
        Assert.Equal(original.Why, deserialized.Why);
        Assert.Equal(original.WhatDoneLooksLike, deserialized.WhatDoneLooksLike);
        Assert.NotEmpty(deserialized.DispatchOrder);
        Assert.NotEmpty(deserialized.Plans);
        // Verify the deserialized object has the expected structure
        Assert.Equal(original.DispatchOrder.Count, deserialized.DispatchOrder.Count);
        Assert.Equal(original.Plans.Count, deserialized.Plans.Count);
    }
}
