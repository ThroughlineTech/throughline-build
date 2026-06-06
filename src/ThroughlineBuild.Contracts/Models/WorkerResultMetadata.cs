namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// Well-known keys and values used in <see cref="WorkerResult.Metadata"/>. Centralized
/// so the producers (the vendor worker agents) and the consumers (the workflow phases)
/// cannot drift on the string literal.
/// </summary>
public static class WorkerResultMetadata
{
    /// <summary>
    /// Metadata key a vendor agent sets when the worker process exited cleanly (exit code 0,
    /// parseable vendor envelope) but emitted no WORKER_RESULT marker. It distinguishes
    /// "the worker forgot the envelope" from an explicit non-Ok envelope, a malformed
    /// envelope, a non-zero exit, or a timeout - all of which mean the tree is not trustworthy.
    /// ImplementPhase reads it to decide whether a committed-but-unreported session can be
    /// salvaged instead of discarded. See TLB-471.
    /// </summary>
    public const string EnvelopeStatusKey = "envelope_status";

    /// <summary>
    /// Value for <see cref="EnvelopeStatusKey"/>: the process exited cleanly but produced no
    /// WORKER_RESULT marker.
    /// </summary>
    public const string EnvelopeMissing = "missing";

    /// <summary>
    /// Value for <see cref="EnvelopeStatusKey"/>: the worker emitted a WORKER_RESULT marker whose
    /// payload parsed as a valid JSON object but omitted the required 'status' field (e.g. a long
    /// session that hand-rolled a non-conforming summary object like {"outcome":"Completed",...}
    /// instead of the prescribed {"status":...,"summary":...} schema). Distinct from
    /// <see cref="EnvelopeMissing"/> (no marker at all), from a JSON syntax error, and from an
    /// explicit non-Ok envelope. ImplementPhase salvages this case the same way as EnvelopeMissing
    /// (clean worktree + HEAD advanced past base, with review as the real gate). The boundary is
    /// deliberately structural - "valid JSON object, no status key" - so a truncated payload or an
    /// unrecognized status enum value (both JsonException) is NOT treated as recoverable. See TLB-476.
    /// </summary>
    public const string EnvelopeMissingStatus = "missing_status";
}
