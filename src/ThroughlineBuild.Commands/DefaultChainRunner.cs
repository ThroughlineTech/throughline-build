using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Default implementation of IChainRunner. Constructs a ChainPhase from the
/// supplied per-phase factories and runs it, invoking the onStep callback after
/// each phase step completes so callers can stream output without buffering.
/// </summary>
public sealed class DefaultChainRunner : IChainRunner
{
    private readonly ChainPhase _chainPhase;

    public DefaultChainRunner(ChainPhase chainPhase)
    {
        _chainPhase = chainPhase;
    }

    public Task<ChainResult> RunAsync(
        string ticketId,
        bool debug,
        Action<string, ChainStep> onStep,
        CancellationToken ct,
        bool noAutoResolve = false,
        ChainBatchImplementGroup? batchImplementGroup = null)
    {
        var options = new ChainPhaseOptions(
            ticketId,
            debug,
            onStep,
            noAutoResolve,
            BatchImplementGroup: batchImplementGroup);
        return _chainPhase.RunAsync(options, ct);
    }
}
