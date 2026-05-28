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
}
