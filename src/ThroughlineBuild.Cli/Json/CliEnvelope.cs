using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

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
}

/// <summary>A machine-readable error: a stable <paramref name="Code"/> plus a human message.</summary>
public sealed record CliError(string Code, string Message);

/// <summary>Uniform failure envelope emitted by any verb run with --json. <c>Ok</c> is always false.</summary>
public sealed record ErrorEnvelope(int SchemaVersion, bool Ok, CliError Error);

/// <summary>A relation edge on a ticket, projected for the wire.</summary>
public sealed record RelationView(string Kind, string TargetId);

/// <summary>A ticket projected for the wire. Mirrors <see cref="Ticket"/>'s scalar fields.</summary>
public sealed record TicketView(
    string Id,
    string Uuid,
    string Title,
    string Type,
    TicketState State,
    Size Size,
    Risk Risk,
    string DescriptionHtml,
    string? ParentId,
    IReadOnlyList<string> Labels,
    IReadOnlyList<RelationView> Relations);

/// <summary>Success envelope for <c>build get --json</c>.</summary>
public sealed record TicketEnvelope(int SchemaVersion, bool Ok, TicketView Data);

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
internal partial class CliJsonContext : JsonSerializerContext { }
