using Xunit;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts.Tests;

public class VerdictTests
{
    private static Verdict MakeVerdict(VerdictKind kind = VerdictKind.Pass) => new Verdict(
        Kind: kind,
        Rationale: "Looks good",
        ChecksFailed: Array.Empty<string>());

    [Fact]
    public void Verdict_EqualWhenSameValues()
    {
        var a = MakeVerdict();
        var b = MakeVerdict();
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Verdict_NotEqualWhenDifferentKind()
    {
        var a = MakeVerdict(VerdictKind.Pass);
        var b = MakeVerdict(VerdictKind.Fail);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verdict_WithExpression_ChangesKind()
    {
        var a = MakeVerdict();
        var b = a with { Kind = VerdictKind.Rework };
        Assert.Equal(VerdictKind.Rework, b.Kind);
        Assert.Equal(VerdictKind.Pass, a.Kind);
    }

    [Fact]
    public void Verdict_ChecksFailedProperty_IsReadOnly()
    {
        var v = MakeVerdict();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(v.ChecksFailed);
    }
}
