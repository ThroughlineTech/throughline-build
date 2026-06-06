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
}
