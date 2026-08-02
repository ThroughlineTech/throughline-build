using System.Text.Json.Serialization;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;

namespace ThroughlineBuild.Cli.Json;

// Versioned JSON envelope for machine-readable command output (build <verb> --json).
// Schema is additive-only: never remove or repurpose a field, only add. stdout carries
// the envelope and nothing else; human diagnostics go to stderr. See TLB-541.
//
// Success envelopes are typed per verb (e.g. TicketEnvelope) so the payload shape is
// statically known and AOT-serializable. Failures are uniform across every verb
// (ErrorEnvelope), so a consumer can parse {ok:false, error:{code,message}} the same way
// regardless of which verb produced it.

/// <summary>Stable error codes carried by a failing envelope's <see cref="CliError.Code"/>.</summary>
public static class CliErrorCodes
{
    public const string Usage = "usage";
    public const string ConfigError = "config_error";
    public const string MissingSecret = "missing_secret";
    public const string NotFound = "not_found";
    public const string Failure = "failure";
    public const string DependencyCycle = "dependency_cycle";
    public const string MissingBase = "missing_base";
    public const string NotGitRepository = "not_git_repository";
    public const string InvalidWorktreeState = "invalid_worktree_state";
    public const string UnhashablePath = "unhashable_path";
}

/// <summary>A machine-readable error: a stable <paramref name="Code"/> plus a human message.</summary>
public sealed record CliError(string Code, string Message);

/// <summary>Uniform failure envelope emitted by any verb run with --json. <c>Ok</c> is always false.</summary>
public sealed record ErrorEnvelope(int SchemaVersion, bool Ok, CliError Error);

/// <summary>A relation edge on a ticket, projected for the wire.</summary>
public sealed record RelationView(string Kind, string TargetId);

/// <summary>A direct child ticket summary for hierarchy verification.</summary>
public sealed record TicketChildView(string Id, string Title, TicketState State);

/// <summary>
/// A ticket projected for the wire. Mirrors <see cref="Ticket"/>'s scalar fields.
/// <see cref="Description"/> is the readable plain-text rendering of the body; the raw
/// Plane editor HTML is kept in <see cref="DescriptionHtml"/> for callers that need markup.
/// </summary>
public sealed record TicketView(
    string Id,
    string Uuid,
    string Title,
    string Type,
    TicketState State,
    Size Size,
    Risk Risk,
    string Description,
    string DescriptionHtml,
    string? ParentId,
    IReadOnlyList<string> Labels,
    IReadOnlyList<RelationView> Relations,
    IReadOnlyList<TicketChildView> Children);

/// <summary>Success envelope for <c>build get --json</c>.</summary>
public sealed record TicketEnvelope(int SchemaVersion, bool Ok, TicketView Data);

// ---- build new - --json (strict JSON draft on stdin) --------------------------------

/// <summary>One relation on a draft: <paramref name="Kind"/> e.g. "blocked_by", toward a ticket id.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketDraftRelation(string? Kind = null, string? TargetId = null);

/// <summary>
/// The strict JSON draft accepted on stdin by <c>build new - --json</c>. Unknown fields are
/// rejected (the agent assembles this; a typo should fail loudly, not get silently dropped).
/// Description and acceptanceCriteria are markdown - the create path renders them to HTML.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketDraft(
    string? Title = null,
    string? Type = null,
    string? Description = null,
    string? AcceptanceCriteria = null,
    IReadOnlyList<string>? Labels = null,
    string? Parent = null,
    IReadOnlyList<TicketDraftRelation>? Relations = null);

/// <summary>Requested identifiers echoed for compatibility with the original create payload.</summary>
public sealed record NewTicketRequestedView(
    IReadOnlyList<string> Labels,
    string? Parent,
    IReadOnlyList<RelationView> Relations);

/// <summary>The data payload returned after creating a ticket.</summary>
public sealed record NewTicketView(
    string Id,
    string Uuid,
    IReadOnlyList<string> Labels,
    string? Parent,
    IReadOnlyList<RelationView> Relations,
    NewTicketRequestedView? Requested = null,
    TicketView? Ticket = null);

/// <summary>Success envelope for <c>build new - --json</c>.</summary>
public sealed record NewTicketEnvelope(int SchemaVersion, bool Ok, NewTicketView Data);

// ---- build list --json -------------------------------------------------------------

/// <summary>A ticket row for <c>build list --json</c> - the lean projection, no description.</summary>
public sealed record ListTicketView(
    string Id,
    string Title,
    TicketState State,
    string Type,
    Size Size,
    Risk Risk,
    IReadOnlyList<string> Labels,
    string? ParentId);

/// <summary>Success envelope for <c>build list --json</c>: an array of ticket rows.</summary>
public sealed record ListEnvelope(int SchemaVersion, bool Ok, IReadOnlyList<ListTicketView> Data);

// ---- build comments --json / build comment --json ----------------------------------

/// <summary>A single comment for <c>build comments --json</c>.</summary>
public sealed record CommentView(string Id, string Body, DateTimeOffset CreatedAt);

/// <summary>Success envelope for <c>build comments --json</c>: an array of comments.</summary>
public sealed record CommentsEnvelope(int SchemaVersion, bool Ok, IReadOnlyList<CommentView> Data);

/// <summary>Data payload after creating a comment - the new comment's id.</summary>
public sealed record CommentCreatedView(string Id);

/// <summary>Success envelope for <c>build comment --json</c>.</summary>
public sealed record CommentCreatedEnvelope(int SchemaVersion, bool Ok, CommentCreatedView Data);

// ---- build transition --json -------------------------------------------------------

/// <summary>Data payload after a state transition.</summary>
public sealed record TransitionView(string Id, TicketState State);

/// <summary>Success envelope for <c>build transition --json</c>.</summary>
public sealed record TransitionEnvelope(int SchemaVersion, bool Ok, TransitionView Data);

// ---- build relate --json -----------------------------------------------------------

/// <summary>A relation row with the stable backend identity required for exact removal.</summary>
public sealed record ManagedRelationView(string Id, string Kind, string TargetId);

/// <summary>Success envelope for <c>build relate ID --list --json</c>.</summary>
public sealed record RelationsEnvelope(int SchemaVersion, bool Ok, IReadOnlyList<ManagedRelationView> Data);

/// <summary>Acknowledgement for relation create/remove.</summary>
public sealed record RelateView(
    string Id,
    string Action,
    string? Kind = null,
    string? TargetId = null,
    string? RelationId = null);

/// <summary>Success envelope for a relation mutation.</summary>
public sealed record RelateEnvelope(int SchemaVersion, bool Ok, RelateView Data);

// ---- build close / defer / reopen / amend --json -----------------------------------

/// <summary>Acknowledgement payload for a lifecycle verb: the ticket id and which action ran.</summary>
public sealed record AckView(string Id, string Action);

/// <summary>Success envelope for the lifecycle verbs (close, defer, reopen, amend).</summary>
public sealed record AckEnvelope(int SchemaVersion, bool Ok, AckView Data);

// ---- build worktree ---------------------------------------------------------------

public sealed record WorktreeLeaseView(string Path, WorktreeLeaseManifest Manifest);
public sealed record WorktreeLeaseEnvelope(int SchemaVersion, bool Ok, WorktreeLeaseView Data);
public sealed record WorktreeListView(
    IReadOnlyList<WorktreeLeaseManifest> Leases,
    IReadOnlyList<string> UnmanifestedDirectories);
public sealed record WorktreeListEnvelope(int SchemaVersion, bool Ok, WorktreeListView Data);
public sealed record WorktreeTeardownView(string Path, string Branch);
public sealed record WorktreeTeardownEnvelope(int SchemaVersion, bool Ok, WorktreeTeardownView Data);

// ---- build candidate ---------------------------------------------------------------

public sealed record CandidateDirtyStateView(
    bool IsDirty,
    bool HasTrackedChanges,
    bool HasStagedChanges,
    bool HasUnstagedChanges,
    bool HasUntrackedFiles,
    bool HasConflicts);

public sealed record CandidateLeaseView(
    bool Present,
    string Path,
    bool? TicketMatches,
    WorktreeLeaseManifest? Manifest);

public sealed record CandidateStatusView(
    string Ticket,
    string BaseRef,
    string BaseSha,
    string HeadSha,
    string? Branch,
    string WorkingDirectory,
    string TrackedDiffHash,
    string CachedDiffHash,
    string UntrackedHash,
    IReadOnlyList<string> TouchedPaths,
    IReadOnlyList<string> UntrackedPaths,
    CandidateLeaseView Lease,
    CandidateDirtyStateView DirtyState);

public sealed record CandidateStatusEnvelope(int SchemaVersion, bool Ok, CandidateStatusView Data);

// ---- build gate -------------------------------------------------------------------

public sealed record GateCheckView(
    string Name,
    string Role,
    string Status,
    int ExitCode,
    long DurationMilliseconds,
    string Stdout,
    string Stderr,
    IReadOnlyList<string> MissingRequiredPaths);

public sealed record GateView(
    string? Ticket,
    string Role,
    string WorkingDirectory,
    bool ChecksConfigured,
    bool Passed,
    string Message,
    IReadOnlyList<GateCheckView> Checks);

public sealed record GateEnvelope(int SchemaVersion, bool Ok, GateView Data);

// ---- build waves ------------------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaveTicketInput(
    string? Id = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Deps = null,
    bool Uncertain = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WavesInput(
    int? Cap = null,
    IReadOnlyList<string>? VerifiedExternalDeps = null,
    IReadOnlyList<WaveTicketInput>? Tickets = null);

public sealed record WavesEnvelope(int SchemaVersion, bool Ok, WavePlan Data);

// ---- build sop doctor -------------------------------------------------------------

public sealed record SopDoctorEnvelope(int SchemaVersion, bool Ok, SopDoctorView Data);

// Source-generated context keeps the --json path statically analyzable under PublishAot=true
// (reflection-based serialization trips IL2026/IL3050). UseStringEnumConverter renders
// State/Size/Risk as their names rather than integers. Mirrors PhaseSummaryJsonContext.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(TicketEnvelope))]
[JsonSerializable(typeof(TicketChildView))]
[JsonSerializable(typeof(TicketDraft))]
[JsonSerializable(typeof(NewTicketEnvelope))]
[JsonSerializable(typeof(NewTicketRequestedView))]
[JsonSerializable(typeof(ListEnvelope))]
[JsonSerializable(typeof(CommentsEnvelope))]
[JsonSerializable(typeof(CommentCreatedEnvelope))]
[JsonSerializable(typeof(TransitionEnvelope))]
[JsonSerializable(typeof(RelationsEnvelope))]
[JsonSerializable(typeof(RelateEnvelope))]
[JsonSerializable(typeof(AckEnvelope))]
[JsonSerializable(typeof(WorktreeLeaseEnvelope))]
[JsonSerializable(typeof(WorktreeLeaseManifest))]
[JsonSerializable(typeof(WorktreeListEnvelope))]
[JsonSerializable(typeof(WorktreeTeardownEnvelope))]
[JsonSerializable(typeof(CandidateStatusEnvelope))]
[JsonSerializable(typeof(GateEnvelope))]
[JsonSerializable(typeof(WavesInput))]
[JsonSerializable(typeof(WaveTicketInput[]))]
[JsonSerializable(typeof(WavesEnvelope))]
[JsonSerializable(typeof(ConductorConfig))]
[JsonSerializable(typeof(SopDoctorEnvelope))]
internal partial class CliJsonContext : JsonSerializerContext { }
