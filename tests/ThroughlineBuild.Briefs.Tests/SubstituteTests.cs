using ThroughlineBuild.Briefs;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class SubstituteTests
{
    [Fact]
    public void Substitute_SimpleVariable_ReplacesPlaceholder()
    {
        var result = "Hello {{name}}".Substitute(
            new Dictionary<string, string> { ["name"] = "world" });

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Substitute_MultipleVariables_ReplacesAll()
    {
        var result = "{{greeting}} {{name}}!".Substitute(
            new Dictionary<string, string> { ["greeting"] = "Hi", ["name"] = "Alice" });

        Assert.Equal("Hi Alice!", result);
    }

    [Fact]
    public void Substitute_RepeatedKey_ReplacesEveryOccurrence()
    {
        var result = "{{x}} and {{x}}".Substitute(
            new Dictionary<string, string> { ["x"] = "foo" });

        Assert.Equal("foo and foo", result);
    }

    [Fact]
    public void Substitute_MissingKey_ThrowsNamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => "{{missing}}".Substitute(new Dictionary<string, string>()));

        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Substitute_ExtraDictionaryKey_IsIgnored()
    {
        var result = "Hello {{name}}".Substitute(
            new Dictionary<string, string> { ["name"] = "world", ["unused"] = "extra" });

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Substitute_EmptyStringValue_SubstitutesAsEmpty()
    {
        var result = "before{{gap}}after".Substitute(
            new Dictionary<string, string> { ["gap"] = "" });

        Assert.Equal("beforeafter", result);
    }

    [Fact]
    public void Substitute_ValueContainingPlaceholder_IsNotReExpanded()
    {
        var result = "{{outer}}".Substitute(
            new Dictionary<string, string> { ["outer"] = "{{inner}}" });

        Assert.Equal("{{inner}}", result);
    }
}
