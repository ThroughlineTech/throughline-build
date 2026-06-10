using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public class ClaudeCodeModelValidatorTests
{
    [Theory]
    [InlineData("haiku")]
    [InlineData("sonnet")]
    [InlineData("opus")]
    [InlineData("Opus")] // alias matching is case-insensitive
    [InlineData("claude-fable-5")]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("anthropic:claude-fable-5")] // provider prefix stripped before checking
    [InlineData("anthropic:sonnet")]
    public void Validate_AcceptsAliasesAndFullClaudeIds(string model)
    {
        Assert.Null(ClaudeCodeModelValidator.Validate(model));
    }

    [Fact]
    public void Validate_RejectsFable_WithFullSlugHint()
    {
        var error = ClaudeCodeModelValidator.Validate("fable");

        Assert.NotNull(error);
        Assert.Contains("claude-fable-5", error);
        Assert.Contains("not a Claude Code tier alias", error);
    }

    [Fact]
    public void Validate_RejectsFable_CaseInsensitive_AndWithProviderPrefix()
    {
        Assert.NotNull(ClaudeCodeModelValidator.Validate("Fable"));
        Assert.NotNull(ClaudeCodeModelValidator.Validate("anthropic:fable"));
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("fastest")]
    public void Validate_RejectsNonClaudeValues_NamingValidForms(string model)
    {
        var error = ClaudeCodeModelValidator.Validate(model);

        Assert.NotNull(error);
        Assert.Contains("haiku, sonnet, opus", error);
        Assert.Contains("claude-fable-5", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrEmpty_ReturnsNull(string? model)
    {
        // Empty values are the config loader's problem (it requires a non-empty model);
        // the validator stays out of the way.
        Assert.Null(ClaudeCodeModelValidator.Validate(model));
    }
}
