using System.Text.Json;
using ThroughlineBuild.Helpers;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class LlmUsageFlattenerTests
{
    [Fact]
    public void Flatten_WithIdictionaryInput_FlattensDictionary()
    {
        // Arrange
        var input = new Dictionary<string, object?>
        {
            { "input_tokens", 100 },
            { "output_tokens", 50 },
            { "model", "claude-3-sonnet" }
        };

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal(100, result["input_tokens"]);
        Assert.Equal(50, result["output_tokens"]);
        Assert.Equal("claude-3-sonnet", result["model"]);
    }

    [Fact]
    public void Flatten_WithJsonElementObjectInput_FlattensObject()
    {
        // Arrange
        var json = "{\"input_tokens\": 100, \"output_tokens\": 50, \"model\": \"claude-3-sonnet\"}";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("input_tokens"));
        Assert.True(result.ContainsKey("output_tokens"));
        Assert.True(result.ContainsKey("model"));
        Assert.Equal("claude-3-sonnet", result["model"]);
    }

    [Fact]
    public void UnwrapJsonElement_WithStringValue_ReturnsString()
    {
        // Arrange
        var json = "\"test string\"";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.IsType<string>(result);
        Assert.Equal("test string", result);
    }

    [Fact]
    public void UnwrapJsonElement_WithInt32Value_ReturnsIntOrLong()
    {
        // Arrange
        using var doc = JsonDocument.Parse("42");
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.True(result is int or long);
        Assert.Equal(42L, Convert.ToInt64(result));
    }

    [Fact]
    public void UnwrapJsonElement_WithInt64Value_ReturnsLong()
    {
        // Arrange
        using var doc = JsonDocument.Parse("9223372036854775807");
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.IsType<long>(result);
        Assert.Equal(9223372036854775807L, (long)result);
    }

    [Fact]
    public void UnwrapJsonElement_WithTrueValue_ReturnsTrue()
    {
        // Arrange
        using var doc = JsonDocument.Parse("true");
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.IsType<bool>(result);
        Assert.True((bool)result);
    }

    [Fact]
    public void UnwrapJsonElement_WithFalseValue_ReturnsFalse()
    {
        // Arrange
        using var doc = JsonDocument.Parse("false");
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.IsType<bool>(result);
        Assert.False((bool)result);
    }

    [Fact]
    public void UnwrapJsonElement_WithNullValue_ReturnsEmptyString()
    {
        // Arrange
        using var doc = JsonDocument.Parse("null");
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(jsonElement);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void UnwrapJsonElement_WithNonJsonElementValue_ReturnsValueAsIs()
    {
        // Arrange
        var value = "direct string";

        // Act
        var result = LlmUsageFlattener.UnwrapJsonElement(value);

        // Assert
        Assert.Equal("direct string", result);
    }

    [Fact]
    public void Flatten_WithNonDictionaryNonJsonElementInput_ReturnsNull()
    {
        // Arrange
        var input = "just a string";

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(input);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Flatten_WithJsonElementArrayInput_ReturnsNull()
    {
        // Arrange
        var json = "[1, 2, 3]";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(jsonElement);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Flatten_WithNullValuesInDictionary_SkipsNullValues()
    {
        // Arrange
        var input = new Dictionary<string, object?>
        {
            { "input_tokens", 100 },
            { "null_field", null },
            { "output_tokens", 50 }
        };

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.False(result.ContainsKey("null_field"));
        Assert.Equal(100, result["input_tokens"]);
        Assert.Equal(50, result["output_tokens"]);
    }

    [Fact]
    public void Flatten_WithEmptyDictionary_ReturnsEmptyDictionary()
    {
        // Arrange
        var input = new Dictionary<string, object?>();

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(input);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Flatten_WithMixedValueTypes_FlattensMixedTypes()
    {
        // Arrange
        var input = new Dictionary<string, object?>
        {
            { "string_val", "text" },
            { "int_val", 42 },
            { "long_val", 9223372036854775807L },
            { "bool_val", true }
        };

        // Act
        var result = LlmUsageFlattener.FlattenLlmUsage(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Count);
        Assert.Equal("text", result["string_val"]);
        Assert.Equal(42, result["int_val"]);
        Assert.Equal(9223372036854775807L, result["long_val"]);
        Assert.True((bool)result["bool_val"]);
    }
}
