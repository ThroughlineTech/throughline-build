using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public class BriefHtmlRendererTests
{
    private static Brief MakeBrief(
        string slug = "my-brief",
        int number = 1,
        string goal = "Implement the feature.",
        IReadOnlyList<string>? inputs = null,
        IReadOnlyList<string>? outputs = null,
        IReadOnlyList<string>? acceptance = null,
        string? notes = null,
        IReadOnlyList<string>? oos = null) =>
        new Brief(
            Slug: slug,
            Number: number,
            Title: slug,
            Goal: goal,
            Inputs: inputs ?? new[] { "Input A", "Input B" },
            Outputs: outputs ?? new[] { "Output X" },
            AcceptanceCriteria: acceptance ?? new[] { "Criterion one" },
            Notes: notes,
            OutOfScope: oos ?? new[] { "No rollback", "No UI" },
            DependsOn: null);

    private static Plan MakePlan(
        string id = "A",
        string name = "Plan Alpha",
        string goal = "Build components.",
        IReadOnlyList<Brief>? briefs = null) =>
        new Plan(
            Id: id,
            Name: name,
            Goal: goal,
            Briefs: briefs ?? Array.Empty<Brief>());

    // ---- RenderBrief tests ----

    [Fact]
    public void RenderBrief_ContainsGoalSection()
    {
        var brief = MakeBrief(goal: "Implement the widget.");
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Goal</h3>", html);
        Assert.Contains("Implement the widget.", html);
    }

    [Fact]
    public void RenderBrief_ContainsInputsSection()
    {
        var brief = MakeBrief(inputs: new[] { "Spec doc", "API schema" });
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Inputs</h3>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("Spec doc", html);
        Assert.Contains("API schema", html);
    }

    [Fact]
    public void RenderBrief_ContainsOutputsSection()
    {
        var brief = MakeBrief(outputs: new[] { "Widget class", "Unit tests" });
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Outputs</h3>", html);
        Assert.Contains("Widget class", html);
        Assert.Contains("Unit tests", html);
    }

    [Fact]
    public void RenderBrief_ContainsAcceptanceSection()
    {
        var brief = MakeBrief(acceptance: new[] { "Widget renders correctly" });
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Acceptance</h3>", html);
        Assert.Contains("Widget renders correctly", html);
    }

    [Fact]
    public void RenderBrief_ContainsNotesWhenPresent()
    {
        var brief = MakeBrief(notes: "Keep it simple for now.");
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Notes</h3>", html);
        Assert.Contains("Keep it simple for now.", html);
    }

    [Fact]
    public void RenderBrief_OmitsNotesWhenAbsent()
    {
        var brief = MakeBrief(notes: null);
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.DoesNotContain("<h3>Notes</h3>", html);
    }

    [Fact]
    public void RenderBrief_ContainsOutOfScopeSection()
    {
        var brief = MakeBrief(oos: new[] { "No caching", "No external calls" });
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<h3>Out of Scope</h3>", html);
        Assert.Contains("No caching", html);
        Assert.Contains("No external calls", html);
    }

    [Fact]
    public void RenderBrief_EscapesHtmlSpecialChars()
    {
        var brief = MakeBrief(goal: "Use <angle> & 'quotes' in goal.");
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("&lt;angle&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<angle>", html);
    }

    [Fact]
    public void RenderBrief_RendersInlineCodeSpans()
    {
        var brief = MakeBrief(goal: "Call `FooBar.Run()` method.");
        var html = BriefHtmlRenderer.RenderBrief(brief);

        Assert.Contains("<code>FooBar.Run()</code>", html);
    }

    // ---- RenderPlan tests ----

    [Fact]
    public void RenderPlan_ContainsGoalSection()
    {
        var plan = MakePlan(goal: "Build alpha components.");
        var html = BriefHtmlRenderer.RenderPlan(plan);

        Assert.Contains("<h3>Goal</h3>", html);
        Assert.Contains("Build alpha components.", html);
    }

    [Fact]
    public void RenderPlan_ContainsBriefList()
    {
        var brief1 = MakeBrief(slug: "alpha-one", number: 1);
        var brief2 = MakeBrief(slug: "alpha-two", number: 2, goal: "Second thing.");
        var plan = MakePlan(briefs: new[] { brief1, brief2 });
        var html = BriefHtmlRenderer.RenderPlan(plan);

        Assert.Contains("<h3>Briefs</h3>", html);
        Assert.Contains("alpha-one", html);
        Assert.Contains("alpha-two", html);
    }

    [Fact]
    public void RenderPlan_EmptyBriefList_NoBriefSection()
    {
        var plan = MakePlan(briefs: Array.Empty<Brief>());
        var html = BriefHtmlRenderer.RenderPlan(plan);

        Assert.DoesNotContain("<h3>Briefs</h3>", html);
    }

    // ---- EscapeHtml helper tests ----

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("<tag>", "&lt;tag&gt;")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("say \"hi\"", "say &quot;hi&quot;")]
    public void EscapeHtml_HandlesEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, BriefHtmlRenderer.EscapeHtml(input));
    }

    // ---- RenderInlineMarkdown helper tests ----

    [Fact]
    public void RenderInlineMarkdown_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BriefHtmlRenderer.RenderInlineMarkdown(""));
    }

    [Fact]
    public void RenderInlineMarkdown_WrapsInParagraph()
    {
        var result = BriefHtmlRenderer.RenderInlineMarkdown("Hello world.");
        Assert.StartsWith("<p>", result);
        Assert.EndsWith("</p>", result);
    }

    [Fact]
    public void RenderInlineMarkdown_RendersCodeSpan()
    {
        var result = BriefHtmlRenderer.RenderInlineMarkdown("Use `foo.Bar()` here.");
        Assert.Contains("<code>foo.Bar()</code>", result);
    }

    [Fact]
    public void RenderInlineMarkdown_EscapesHtmlOutsideCode()
    {
        var result = BriefHtmlRenderer.RenderInlineMarkdown("Use <br> tag.");
        Assert.Contains("&lt;br&gt;", result);
        Assert.DoesNotContain("<br>", result);
    }

    [Fact]
    public void RenderInlineMarkdown_UnmatchedBacktick_TreatsAsLiteral()
    {
        var result = BriefHtmlRenderer.RenderInlineMarkdown("price is 50` per unit");
        Assert.Contains("50", result);
        // Should not throw or produce broken HTML
        Assert.StartsWith("<p>", result);
    }
}
