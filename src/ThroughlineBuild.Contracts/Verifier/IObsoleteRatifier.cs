using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

public interface IObsoleteRatifier
{
    // evidenceDirectory: the worktree the escalation was raised in. The cited commit and
    // files live there (a chain rework round runs in the ticket's shared worktree, where
    // new files are not yet present in the main worktree). When null, evidence is resolved
    // against the ratifier's construction-time working directory.
    Task<Verdict> RatifyAsync(Ticket ticket, WorkerResult escalateResult, string? evidenceDirectory, CancellationToken ct);
}
