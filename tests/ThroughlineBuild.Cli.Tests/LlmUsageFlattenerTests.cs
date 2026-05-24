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
        var result = LlmUsageFlattener.Flatten(input);

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
        var result = LlmUsageFlattener.Flatten(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("input_tokens"));
        Assert.True(result.ContainsKey("output_tokens"));
        Assert.True(result.ContainsKey("model"));
        Assert.Equal("claude-3-sonnet", result["model"]);
    }

    [Fact]
    public void Flatten_WithJsonElementStringValues_UnwrapsStrings()
    {
        // Arrange
        var json = "{\"field1\": \"test string\", \"field2\": \"another string\"}";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.Flatten(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.IsType<string>(result["field1"]);
        Assert.Equal("test string", result["field1"]);
        Assert.Equal("another string", result["field2"]);
    }

    [Fact]
    public void Flatten_WithJsonElementNumberValues_UnwrapsNumbers()
    {
        // Arrange
        var json = "{\"int_field\": 42, \"long_field\": 9223372036854775807}";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.Flatten(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.True(result["int_field"] is int or long);
        Assert.Equal(42L, Convert.ToInt64(result["int_field"]));
        Assert.True(result["long_field"] is int or long);
        Assert.Equal(9223372036854775807L, Convert.ToInt64(result["long_field"]));
    }

    [Fact]
    public void Flatten_WithJsonElementBoolValues_UnwrapsBooleans()
    {
        // Arrange
        var json = "{\"true_field\": true, \"false_field\": false}";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.Flatten(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.IsType<bool>(result["true_field"]);
        Assert.True((bool)result["true_field"]);
        Assert.IsType<bool>(result["false_field"]);
        Assert.False((bool)result["false_field"]);
    }

    [Fact]
    public void Flatten_WithJsonElementNullValues_UnwrapsNullAsEmptyString()
    {
        // Arrange
        var json = "{\"null_field\": null, \"real_field\": \"value\"}";
        using var doc = JsonDocument.Parse(json);
        var jsonElement = doc.RootElement;

        // Act
        var result = LlmUsageFlattener.Flatten(jsonElement);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("", result["null_field"]);
        Assert.Equal("value", result["real_field"]);
    }

    [Fact]
    public void Flatten_WithNonDictionaryNonJsonElementInput_ReturnsNull()
    {
        // Arrange
        var input = "just a string";

        // Act
        var result = LlmUsageFlattener.Flatten(input);

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
        var result = LlmUsageFlattener.Flatten(jsonElement);

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
        var result = LlmUsageFlattener.Flatten(input);

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
        var result = LlmUsageFlattener.Flatten(input);

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
        var result = LlmUsageFlattener.Flatten(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Count);
        Assert.Equal("text", result["string_val"]);
        Assert.Equal(42, result["int_val"]);
        Assert.Equal(9223372036854775807L, result["long_val"]);
        Assert.True((bool)result["bool_val"]);
    }
}
