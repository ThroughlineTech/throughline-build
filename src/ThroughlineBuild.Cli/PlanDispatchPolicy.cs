namespace ThroughlineBuild.Cli;

internal static class PlanDispatchPolicy
{
    public static bool ShouldPromote(string verb, bool fromBrief, bool configuredPromote) =>
        fromBrief || (verb == "chain" && configuredPromote);
}
