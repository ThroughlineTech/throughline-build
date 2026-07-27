using ThroughlineBuild.Contracts;
using ThroughlineBuild.Git;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Composition helper for the chain verb: wires an already-resolved set of dependencies into a
/// <see cref="ChainPhase"/>. Extracted from RunChainVerbAsync so the construction has a single,
/// test-coverable seam. The original --batch-implement bug (the batchWorker argument omitted from
/// the inline ChainPhase construction) went undetected for the feature's whole life precisely
/// because the composition root had no test; building the phase here lets one Cli.Tests check fail
/// if a required dependency is ever dropped again. This is a pure extraction - it only news up the
/// ChainPhase the inline code built before, with identical arguments.
/// </summary>
internal static class ChainPhaseComposition
{
    /// <summary>
    /// Builds the ChainPhase used by the chain verb from resolved dependencies. The batch-implement
    /// worker is created here from the same implement agent the per-ticket implement factory uses;
    /// dropping it leaves the batch path in RunParentChainAsync unreachable.
    /// </summary>
    internal static ChainPhase BuildChainPhase(
        ITicketing ticketing,
        IEventSink eventSink,
        BuildOptions buildOptions,
        Func<BuildOptions, PlanPhase> planFactory,
        Func<BuildOptions, ImplementPhaseOptions, ImplementPhase> implementFactory,
        Func<BuildOptions, GateOutcome?, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<BuildOptions, ShipPhase> chainShipFactory,
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory,
        string workingDirectory,
        IWorkerAgentFactory workerFactory,
        Func<string, string> effectiveAgentFor,
        string? landingRemote,
        bool landingPushEnabled,
        // The deterministic gate (op-30) runs between implement and review. Optional: null leaves the
        // chain on its pre-gate path. Forwarded through the same single composition seam as the batch
        // worker so neither can be silently dropped from the ChainPhase construction.
        Func<BuildOptions, GatePhase>? gateFactory = null,
        // Post-rework check re-run: the configured review checks, re-run selectively after a
        // check-driven rework round so a fix that still fails its check loops straight back to
        // the worker instead of burning a verifier call. Null disables the recheck.
        IReadOnlyList<CheckSpec>? reworkRecheckSpecs = null) =>
        new ChainPhase(
            new ChainPhaseCoreDependencies
            {
                Ticketing = ticketing,
                Events = eventSink,
                BaseOptions = buildOptions,
                Git = new ProcessGitClient(),
                SessionIdGenerator = () => Guid.NewGuid().ToString("N"),
                WorkingDirectory = workingDirectory
            },
            new ChainPhaseFactories
            {
                Plan = planFactory,
                Implement = implementFactory,
                Review = reviewFactory,
                Ship = shipFactory,
                ChainShip = chainShipFactory,
                Gate = gateFactory,
                Ratifier = ratifierFactory
            },
            new ChainPhaseExecutionDependencies
            {
                FeedbackRetriever = null,
                // A declared batch group dispatches one warm implement session. Reuse the
                // per-ticket implement agent so the batch path cannot silently disappear.
                BatchWorker = workerFactory.Create(
                    effectiveAgentFor("implement")),
                LandingRemote = landingRemote,
                LandingPushEnabled = landingPushEnabled,
                ReworkRecheckSpecs = reworkRecheckSpecs,
                ReworkRecheckRunner =
                    reworkRecheckSpecs is { Count: > 0 }
                        ? new AutomatedChecksRunner()
                        : null,
                Output = null
            });
}
