using System.IO;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Helpers.Tests;

public class PrefixedTextWriterTests
{
    [Fact]
    public void WriteLine_prepends_prefix()
    {
        var inner = new StringWriter();
        var writer = new PrefixedTextWriter("[TLB-1] ", inner);

        writer.WriteLine("hello");

        Assert.Equal("[TLB-1] hello" + writer.NewLine, inner.ToString());
    }

    [Fact]
    public void WriteLine_null_value_writes_prefix_only()
    {
        var inner = new StringWriter();
        var writer = new PrefixedTextWriter("[TLB-2] ", inner);

        writer.WriteLine((string?)null);

        Assert.Equal("[TLB-2] " + writer.NewLine, inner.ToString());
    }

    [Fact]
    public void Null_inner_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PrefixedTextWriter("[X] ", null!));
    }

    [Fact]
    public void Write_char_delegates_without_prefix()
    {
        var inner = new StringWriter();
        var writer = new PrefixedTextWriter("[TLB-3] ", inner);

        writer.Write('A');
        writer.Write('B');

        Assert.Equal("AB", inner.ToString());
    }

    [Fact]
    public void Encoding_matches_inner()
    {
        var inner = new StringWriter();
        var writer = new PrefixedTextWriter("[X] ", inner);

        Assert.Equal(inner.Encoding, writer.Encoding);
    }
}
