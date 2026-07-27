using ThroughlineBuild.Contracts;
using Xunit;

namespace ThroughlineBuild.Contracts.Tests;

public class RelationKindsTests
{
    public static IEnumerable<object[]> SupportedVariants()
    {
        foreach (var canonical in RelationKinds.Allowed)
        {
            var variants = new[]
            {
                canonical,
                canonical.Replace('_', ' '),
                canonical.Replace('_', '-'),
            };

            foreach (var variant in variants.Distinct(StringComparer.Ordinal))
                yield return new object[] { variant, canonical };
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
