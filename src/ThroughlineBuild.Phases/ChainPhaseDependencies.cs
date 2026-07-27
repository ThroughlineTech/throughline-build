using ThroughlineBuild.Contracts;
using ThroughlineBuild.Verification;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Core runtime collaborators used throughout one chain.
/// </summary>
public sealed record ChainPhaseCoreDependencies
{
    public required ITicketing Ticketing { get; init; }
    public required IEventSink Events { get; init; }
    public required BuildOptions BaseOptions { get; init; }
    public required IGitClient Git { get; init; }
    public required Func<string> SessionIdGenerator { get; init; }
    public required string WorkingDirectory { get; init; }
}

/// <summary>
/// Factories for every phase a chain can dispatch.
/// Nullable factories are explicit capabilities and must still be supplied.
/// </summary>
public sealed record ChainPhaseFactories
{
    public required Func<BuildOptions, PlanPhase> Plan { get; init; }
    public required Func<
        BuildOptions,
        ImplementPhaseOptions,
        ImplementPhase> Implement { get; init; }
    public required Func<
        BuildOptions,
        GateOutcome?,
        ReviewPhase> Review { get; init; }
    public required Func<BuildOptions, ShipPhase> Ship { get; init; }
    public required Func<BuildOptions, ShipPhase>? ChainShip { get; init; }
    public required Func<BuildOptions, GatePhase>? Gate { get; init; }
    public required Func<
        BuildOptions,
        IObsoleteRatifier>? Ratifier { get; init; }
}

/// <summary>
/// Optional chain capabilities and operator-facing execution settings.
/// Every property is required so omissions are compile-time errors.
/// </summary>
public sealed record ChainPhaseExecutionDependencies
{
    public required IReviewFeedbackRetriever? FeedbackRetriever { get; init; }
    public required IWorkerAgent? BatchWorker { get; init; }
    public required string? LandingRemote { get; init; }
    public required bool LandingPushEnabled { get; init; }
    public required IReadOnlyList<CheckSpec>? ReworkRecheckSpecs { get; init; }
    public required AutomatedChecksRunner? ReworkRecheckRunner { get; init; }
    public required TextWriter? Output { get; init; }
}
