namespace ThroughlineBuild.Contracts.Models;

/// <summary>
/// Side-channel keying metadata stamped onto a worker debug transcript (the
/// <c>transcript.jsonl</c> written under <see cref="WorkerOptions.DebugCaptureDirectory"/>
/// when --debug is on). Carries only the identifiers a worker agent cannot derive on its
/// own from the brief or the stream it parses: the engine build sha, the orchestrator
/// session id, and the rework-round counter. The ticket id and phase come from the brief,
/// the worker name from the agent, and the model from the stream's system event - so they
/// are intentionally NOT duplicated here.
///
/// This is pure observation. Nothing here enters the worker's prompt or alters its
/// behavior; it exists only so cross-run analysis can A/B the same build + model. All
/// fields are optional - a null context means "record what is derivable, omit the rest".
/// op-doc sha/hash is deliberately absent: the op-doc only exists at scaffold time, the
/// chain runs against a ticket, and stamping it anywhere the worker can see would perturb
/// the very thing being measured.
/// </summary>
/// <param name="BuildVersion">The engine build version/sha (e.g. "0.1.0+d0ee732"), as
/// surfaced by <c>build --version</c>. The key that lets analysis hold the build constant
/// across runs.</param>
/// <param name="SessionId">The orchestrator session id for the phase that spawned this
/// worker (per-chain / per-phase). Null when not known at the construction site.</param>
/// <param name="ReworkRound">The rework round number for an implement re-dispatch (0 or
/// null for the initial round; >= 1 for each rework). Null for phases without a rework
/// concept.</param>
public sealed record DebugTranscriptContext(
    string? BuildVersion = null,
    string? SessionId = null,
    int? ReworkRound = null);
