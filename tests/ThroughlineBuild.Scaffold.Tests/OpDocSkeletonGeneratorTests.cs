using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

public sealed class OpDocSkeletonGeneratorTests
{
    [Fact]
    public void Render_UsesRequestedSlugInOperationHeader()
    {
        var markdown = OpDocSkeletonGenerator.Render("my-slug");

        Assert.StartsWith("# Operation: my-slug", markdown);
    }

    [Fact]
    public void Render_ProducesSkeletonThatParsesAndValidatesCleanly()
    {
        var markdown = OpDocSkeletonGenerator.Render("my-slug");

        var parseResult = OpDocParser.ParseLines(markdown.Replace("\r\n", "\n").Split('\n'));
        Assert.Empty(parseResult.Errors);
        Assert.NotNull(parseResult.Parsed);

        var validationResult = OpDocValidator.Validate(parseResult.Parsed!);
        Assert.Empty(validationResult.Errors);
        Assert.Empty(validationResult.Warnings);
    }

    [Fact]
    public void Render_IncludesRequiredSectionsInSpecOrder()
    {
        var markdown = OpDocSkeletonGenerator.Render("my-slug");

        AssertInOrder(
            markdown,
            "# Operation: my-slug",
            "## Why this exists",
            "## Dispatch order",
            "## Plan A: First plan",
            "### Goal",
            "### Briefs",
            "### Briefs - detail",
            "#### Brief 01: first-brief",
            "Goal:",
            "Inputs:",
            "Outputs:",
            "Acceptance:",
            "OOS:",
            "## What done looks like");

        Assert.EndsWith(
            "## What done looks like\n\nPlaceholder done state. Replace this with the observable end state for the operation.\n",
            markdown.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("My-Slug")]
    [InlineData("my_slug")]
    [InlineData("my--slug")]
    [InlineData("my-slug-")]
    [InlineData("1-my-slug")]
    public void Render_RejectsEmptyOrNonKebabSlug(string slug)
    {
        Assert.False(OpDocSkeletonGenerator.IsValidSlug(slug));
        Assert.Throws<ArgumentException>(() => OpDocSkeletonGenerator.Render(slug));
    }

    private static void AssertInOrder(string content, params string[] expected)
    {
        var startAt = 0;
        foreach (var text in expected)
        {
            var index = content.IndexOf(text, startAt, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected '{text}' after offset {startAt}.");
            startAt = index + text.Length;
        }
    }
}
