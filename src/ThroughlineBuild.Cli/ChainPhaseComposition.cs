using ThroughlineBuild.Contracts;
using ThroughlineBuild.Phases;

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
        Func<BuildOptions, ReviewPhase> reviewFactory,
        Func<BuildOptions, ShipPhase> shipFactory,
        Func<BuildOptions, ShipPhase> chainShipFactory,
        Func<BuildOptions, IObsoleteRatifier> ratifierFactory,
        string workingDirectory,
        IWorkerAgentFactory workerFactory,
        Func<string, string> effectiveAgentFor,
        string? landingRemote,
        bool landingPushEnabled) =>
        new ChainPhase(
            ticketing,
            eventSink,
            buildOptions,
            planFactory,
            implementFactory,
            reviewFactory,
            shipFactory,
            workingDirectory: workingDirectory,
            ratifierFactory: ratifierFactory,
            chainShipFactory: chainShipFactory,
            // Batch-implement worker: a declared batch group dispatches ONE warm implement session
            // instead of a cold per-ticket implement for each group member. Reuse the same agent the
            // per-ticket implement factory uses so batch and non-batch implement share a worker;
            // without this the batch path in RunParentChainAsync is unreachable and --batch-implement
            // silently degrades to per-ticket.
            batchWorker: workerFactory.Create(effectiveAgentFor("implement")),
            // Root-chain landing: the outermost chain fast-forwards its accumulated integration
            // branch onto the configured target and pushes here (intermediate chain ships run
            // NoPush). Push honors the same --no-push / [ship] push toggle the per-ticket ship uses.
            landingRemote: landingRemote,
            landingPushEnabled: landingPushEnabled);
}
