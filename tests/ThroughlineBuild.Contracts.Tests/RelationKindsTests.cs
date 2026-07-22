using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class RelationKindsTests
{
    public static IEnumerable<object[]> SupportedVariants()
    {
        foreach (var canonical in RelationKinds.Allowed)
        {
            yield return new object[] { canonical, canonical };
            yield return new object[] { canonical.Replace('_', ' '), canonical };
            yield return new object[] { canonical.Replace('_', '-'), canonical };
        }
    }

    [Theory]
    [MemberData(nameof(SupportedVariants))]
    public void TryNormalize_AcceptsSupportedVariants(string input, string expected)
    {
        Assert.True(RelationKinds.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryNormalize_RejectsUnknownType() =>
        Assert.False(RelationKinds.TryNormalize("blocks", out _));
}
