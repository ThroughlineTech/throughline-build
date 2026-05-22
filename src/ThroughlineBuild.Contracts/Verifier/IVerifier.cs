using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Contracts;

public interface IVerifier
{
    Task<Verdict> VerifyAsync(
        Brief brief,
        GitDiff diff,
        WorkerResult workerResult,
        CancellationToken ct);
}

public record GitDiff(
    string FromRef,
    string ToRef,
    IReadOnlyList<DiffEntry> Entries);

public record DiffEntry(
    string Path,
    DiffKind Kind,
    string? OldPath,
    int LinesAdded,
    int LinesRemoved,
    string? PatchContent);

public enum DiffKind { Added, Modified, Deleted, Renamed }
