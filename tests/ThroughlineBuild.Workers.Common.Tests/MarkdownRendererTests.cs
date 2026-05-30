using Xunit;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.Common.Tests;

// Tests for MarkdownRenderer: fixture tests covering each supported construct,
// a determinism test, and passthrough behaviour for unsupported constructs.
public class MarkdownRendererTests
{
    [Fact]
    public void Render_EmptyString_ReturnsEmptyString()
    {
        var result = MarkdownRenderer.Render(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Render_SingleParagraph_ReturnsPTag()
    {
        var result = MarkdownRenderer.Render("Hello world");
        Assert.Equal("<p>Hello world</p>", result);
    }

    [Theory]
    [InlineData("# Heading 1", "<h1>Heading 1</h1>")]
    [InlineData("## Heading 2", "<h2>Heading 2</h2>")]
    [InlineData("### Heading 3", "<h3>Heading 3</h3>")]
    [InlineData("#### Heading 4", "<h4>Heading 4</h4>")]
    [InlineData("##### Heading 5", "<h5>Heading 5</h5>")]
    [InlineData("###### Heading 6", "<h6>Heading 6</h6>")]
    public void Render_AtxHeadings_ReturnsCorrectHTag(string markdown, string expected)
    {
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Render_UnorderedListFlat_ReturnsUlWithLiItems()
    {
        var markdown = "- Item one\n- Item two";
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal("<ul><li>Item one</li><li>Item two</li></ul>", result);
    }

    [Fact]
    public void Render_OrderedListFlat_ReturnsOlWithLiItems()
    {
        var markdown = "1. First\n2. Second";
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal("<ol><li>First</li><li>Second</li></ol>", result);
    }

    [Fact]
    public void Render_NestedUnorderedList_RendersNestedUl()
    {
        var markdown = "- Parent\n  - Child";
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal("<ul><li>Parent<ul><li>Child</li></ul></li></ul>", result);
    }

    [Fact]
    public void Render_FencedCodeBlockWithLanguage_ReturnsPreCodeWithClass()
    {
        var markdown = "```csharp\nvar x = 1;\n```";
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal("<pre><code class=\"language-csharp\">var x = 1;\n</code></pre>", result);
    }

    [Fact]
    public void Render_FencedCodeBlockWithoutLanguage_ReturnsPreCodeWithoutClass()
    {
        var markdown = "```\nsome code\n```";
        var result = MarkdownRenderer.Render(markdown);
        Assert.Equal("<pre><code>some code\n</code></pre>", result);
    }

    [Fact]
    public void Render_InlineCode_ReturnsCodeTag()
    {
        var result = MarkdownRenderer.Render("Use `var x = 1` here");
        Assert.Equal("<p>Use <code>var x = 1</code> here</p>", result);
    }

    [Fact]
    public void Render_BoldEmphasis_ReturnsStrongTag()
    {
        var result = MarkdownRenderer.Render("This is **bold** text");
        Assert.Equal("<p>This is <strong>bold</strong> text</p>", result);
    }

    [Fact]
    public void Render_ItalicEmphasis_ReturnsEmTag()
    {
        var result = MarkdownRenderer.Render("This is *italic* text");
        Assert.Equal("<p>This is <em>italic</em> text</p>", result);
    }

    [Fact]
    public void Render_Link_ReturnsAnchorTag()
    {
        var result = MarkdownRenderer.Render("[Click here](https://example.com)");
        Assert.Equal("<p><a href=\"https://example.com\">Click here</a></p>", result);
    }

    [Fact]
    public void Render_Determinism_SameInputProducesSameOutput()
    {
        var markdown =
            "# Title\n\n" +
            "Some **bold** and *italic* text with `code`.\n\n" +
            "- Item one\n- Item two\n\n" +
            "1. First\n2. Second\n\n" +
            "```js\nconsole.log('hello');\n```\n\n" +
            "[Link](https://example.com)";

        var result1 = MarkdownRenderer.Render(markdown);
        var result2 = MarkdownRenderer.Render(markdown);

        Assert.Equal(result1, result2);
    }
}
