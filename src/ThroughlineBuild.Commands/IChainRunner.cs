using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;

namespace ThroughlineBuild.Commands;

/// <summary>
/// Runs a full chain for a single ticket, invoking the onStep callback after each
/// phase completes so callers can stream per-phase output without buffering the
/// entire ChainResult first.
/// </summary>
public interface IChainRunner
{
    Task<ChainResult> RunAsync(
        string ticketId,
        bool debug,
        Action<string, ChainStep> onStep,
        CancellationToken ct,
        bool noAutoResolve = false,
        ChainBatchImplementGroup? batchImplementGroup = null);
}
