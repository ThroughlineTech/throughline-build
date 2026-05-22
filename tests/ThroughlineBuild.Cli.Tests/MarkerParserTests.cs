using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class MarkerParserTests
{
    [Fact]
    public void Parse_SingleMarkerWithValue_ReturnsParsedMarker()
    {
        // Arrange
        var input = "[planned_at: abc123]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Single(result);
        Assert.Equal("planned_at", result[0].Name);
        Assert.Equal("abc123", result[0].Value);
    }

    [Fact]
    public void Parse_SingleMarkerWithoutValue_ReturnsParsedMarkerWithNullValue()
    {
        // Arrange
        var input = "[implemented]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Single(result);
        Assert.Equal("implemented", result[0].Name);
        Assert.Null(result[0].Value);
    }

    [Fact]
    public void Parse_MultipleMarkers_ReturnsAllMarkers()
    {
        // Arrange
        var input = "[foo: bar] [baz] [qux: 123]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("foo", result[0].Name);
        Assert.Equal("bar", result[0].Value);
        Assert.Equal("baz", result[1].Name);
        Assert.Null(result[1].Value);
        Assert.Equal("qux", result[2].Name);
        Assert.Equal("123", result[2].Value);
    }

    [Fact]
    public void Parse_MarkersWithHtmlTags_StripHtmlAndParseMarkers()
    {
        // Arrange
        var input = "<p>[status: active]</p> Some text <div>[priority: high]</div>";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("status", result[0].Name);
        Assert.Equal("active", result[0].Value);
        Assert.Equal("priority", result[1].Name);
        Assert.Equal("high", result[1].Value);
    }

    [Fact]
    public void Parse_MalformedMarkers_SkipInvalidOnes()
    {
        // Arrange
        var input = "[1invalid: test] [@invalid: foo] [valid: ok] [_valid_name]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("valid", result[0].Name);
        Assert.Equal("ok", result[0].Value);
        Assert.Equal("_valid_name", result[1].Name);
        Assert.Null(result[1].Value);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyList()
    {
        // Arrange
        var input = "";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NullString_ReturnsEmptyList()
    {
        // Arrange
        string? input = null;

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NoMarkers_ReturnsEmptyList()
    {
        // Arrange
        var input = "Just some regular text with no markers in it";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_MarkerWithSpacesAroundColon_ParsesCorrectly()
    {
        // Arrange
        var input = "[name  :  value with spaces  ]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Single(result);
        Assert.Equal("name", result[0].Name);
        Assert.Equal("value with spaces", result[0].Value);
    }

    [Fact]
    public void Parse_MarkerNameWithUnderscores_ParsesCorrectly()
    {
        // Arrange
        var input = "[_private_flag] [__double__underscore: value]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("_private_flag", result[0].Name);
        Assert.Equal("__double__underscore", result[1].Name);
        Assert.Equal("value", result[1].Value);
    }

    [Fact]
    public void Parse_InvalidMarkerName_SkippedSilently()
    {
        // Arrange
        var input = "[123invalid: value] [valid_name: ok] [-hyphen: bad]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Single(result);
        Assert.Equal("valid_name", result[0].Name);
        Assert.Equal("ok", result[0].Value);
    }

    [Fact]
    public void Parse_ComplexHtmlWithMultipleMarkers_ExtractsAllMarkers()
    {
        // Arrange
        var input = "<div class=\"comment\"><p>Status: [status: in_progress]</p><p>Tags: [tag: urgent] [tag: high_priority]</p></div>";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("status", result[0].Name);
        Assert.Equal("in_progress", result[0].Value);
        Assert.Equal("tag", result[1].Name);
        Assert.Equal("urgent", result[1].Value);
        Assert.Equal("tag", result[2].Name);
        Assert.Equal("high_priority", result[2].Value);
    }

    [Fact]
    public void Parse_MarkerValueWithBrackets_ParsesToFirstClosingBracket()
    {
        // Arrange
        var input = "[code: [some code]]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.Single(result);
        Assert.Equal("code", result[0].Name);
        Assert.Equal("[some code", result[0].Value);
    }

    [Fact]
    public void Parse_ReturnsReadOnlyList()
    {
        // Arrange
        var input = "[test: value]";

        // Act
        var result = MarkerParser.Parse(input);

        // Assert
        Assert.IsAssignableFrom<IReadOnlyList<Marker>>(result);
    }
}
