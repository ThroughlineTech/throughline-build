using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class TemplateLoaderTests
{
    [Fact]
    public void Load_KnownTemplate_ReturnsContent()
    {
        var content = TemplateLoader.Load("claude-code", "plan.md");

        Assert.Contains("{{ticket_id}}", content);
        Assert.Contains("{{title}}", content);
    }

    [Fact]
    public void Load_UnknownTemplate_ThrowsWithAvailableList()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TemplateLoader.Load("claude-code", "does-not-exist.md"));

        Assert.Contains("does-not-exist.md", ex.Message);
        Assert.Contains("plan.md", ex.Message);
        Assert.Contains("implement.md", ex.Message);
        Assert.Contains("review.md", ex.Message);
    }

    [Fact]
    public void Load_CalledTwice_ReturnsSameContent()
    {
        var first = TemplateLoader.Load("claude-code", "plan.md");
        var second = TemplateLoader.Load("claude-code", "plan.md");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Load_GeminiDraft_ReturnsNonEmptyContent()
    {
        var content = TemplateLoader.Load("gemini", "draft.md");

        Assert.False(string.IsNullOrEmpty(content));
    }

    [Fact]
    public void Load_GeminiPlan_ReturnsNonEmptyContent()
    {
        var content = TemplateLoader.Load("gemini", "plan.md");

        Assert.False(string.IsNullOrEmpty(content));
    }

    [Fact]
    public void Load_GeminiImplement_ReturnsNonEmptyContent()
    {
        var content = TemplateLoader.Load("gemini", "implement.md");

        Assert.False(string.IsNullOrEmpty(content));
    }

    [Fact]
    public void Load_GeminiReview_ReturnsNonEmptyContent()
    {
        var content = TemplateLoader.Load("gemini", "review.md");

        Assert.False(string.IsNullOrEmpty(content));
    }
}
