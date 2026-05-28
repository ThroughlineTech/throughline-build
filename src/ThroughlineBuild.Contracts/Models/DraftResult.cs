namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// Outcome of a DraftPhase.RunAsync invocation.
/// </summary>
public enum DraftOutcome
{
    /// <summary>Worker returned a well-formed body markdown.</summary>
    Ok,
    /// <summary>Operator text was empty or whitespace; worker was not invoked.</summary>
    EmptyInput,
    /// <summary>Worker subprocess returned a non-Ok status.</summary>
    WorkerFailed,
    /// <summary>Worker output was missing required sections (title or description).</summary>
    InvalidWorkerOutput
}

/// <summary>
/// Result returned by DraftPhase.RunAsync.
/// </summary>
/// <param name="Outcome">Categorised outcome of the draft operation.</param>
/// <param name="BodyMarkdown">Full template-formatted ticket body when Outcome=Ok; null otherwise.</param>
/// <param name="FailureReason">Human-readable reason when Outcome != Ok; null on success.</param>
public sealed record DraftResult(
    DraftOutcome Outcome,
    string? BodyMarkdown,
    string? FailureReason);
