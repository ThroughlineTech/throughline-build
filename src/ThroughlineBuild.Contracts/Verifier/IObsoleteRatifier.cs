using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

public interface IObsoleteRatifier
{
    Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, CancellationToken ct);
}
