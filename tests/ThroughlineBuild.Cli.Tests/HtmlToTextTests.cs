using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

// HtmlToText turns Plane's stored comment HTML back into readable plain text (TLB-541).
public class HtmlToTextTests
{
    [Fact]
    public void Paragraph_BecomesPlainText()
    {
        Assert.Equal("developer", HtmlToText.Render("<p>developer</p>"));
    }

    [Fact]
    public void InlineTags_AreDroppedButTextKept()
    {
        Assert.Equal("done - see Foo.cs",
            HtmlToText.Render("<p><strong>done</strong> - see <code>Foo.cs</code></p>"));
    }

    [Fact]
    public void Entities_AreDecoded()
    {
        Assert.Equal("a < b && c > d",
            HtmlToText.Render("<p>a &lt; b &amp;&amp; c &gt; d</p>"));
    }

    [Fact]
    public void Lists_BecomeDashLines()
    {
        var text = HtmlToText.Render("<ul><li>first</li><li>second</li></ul>");
        Assert.Equal("- first\n- second", text);
    }

    [Fact]
    public void Paragraphs_AreSeparatedByBlankLine()
    {
        var text = HtmlToText.Render("<p>one</p><p>two</p>");
        Assert.Equal("one\n\ntwo", text);
    }

    [Fact]
    public void BrBecomesNewline()
    {
        Assert.Equal("line one\nline two", HtmlToText.Render("line one<br/>line two"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsEmpty(string? html)
    {
        Assert.Equal(string.Empty, HtmlToText.Render(html));
    }

    [Fact]
    public void PlainText_PassesThrough()
    {
        Assert.Equal("just text", HtmlToText.Render("just text"));
    }
}
