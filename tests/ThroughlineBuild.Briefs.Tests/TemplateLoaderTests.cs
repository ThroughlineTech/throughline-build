using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class TemplateLoaderTests
{
    [Fact]
    public void Load_KnownTemplate_ReturnsContent()
    {
        var content = TemplateLoader.Load("fixture.md");

        Assert.Contains("Hello", content);
        Assert.Contains("{{name}}", content);
    }

    [Fact]
    public void Load_UnknownTemplate_ThrowsWithAvailableList()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TemplateLoader.Load("does-not-exist.md"));

        Assert.Contains("does-not-exist.md", ex.Message);
        Assert.Contains("fixture.md", ex.Message);
    }

    [Fact]
    public void Load_CalledTwice_ReturnsSameContent()
    {
        var first = TemplateLoader.Load("fixture.md");
        var second = TemplateLoader.Load("fixture.md");

        Assert.Equal(first, second);
    }
}
