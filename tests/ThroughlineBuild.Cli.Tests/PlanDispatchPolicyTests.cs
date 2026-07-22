using ThroughlineBuild.Cli;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

public class PlanDispatchPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandalonePlan_IgnoresConfiguredMode_AndInvestigates(bool configuredPromote)
    {
        Assert.False(PlanDispatchPolicy.ShouldPromote("plan", fromBrief: false, configuredPromote));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Chain_HonorsConfiguredMode(bool configuredPromote, bool expected)
    {
        Assert.Equal(expected, PlanDispatchPolicy.ShouldPromote("chain", fromBrief: false, configuredPromote));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("chain")]
    public void FromBrief_ExplicitlyPromotesForEitherVerb(string verb)
    {
        Assert.True(PlanDispatchPolicy.ShouldPromote(verb, fromBrief: true, configuredPromote: false));
    }
}
