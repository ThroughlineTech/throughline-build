using System.Text.Json.Serialization;

namespace ThroughlineBuild.Helpers;

// Source-generated JSON context for the --summary-json output path. The CLI publishes
// with PublishAot=true, so reflection-based JsonSerializer.Serialize trips IL2026/IL3050
// trim/AOT analysis warnings. Routing the renderer through this context keeps that path
// statically analyzable. The options below mirror what PhaseSummaryRenderer used before
// (indented, omit-nulls) so the JSON wire shape is unchanged.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PhaseSummary))]
[JsonSerializable(typeof(PlanPhaseSummary))]
[JsonSerializable(typeof(ImplementPhaseSummary))]
[JsonSerializable(typeof(ReviewPhaseSummary))]
[JsonSerializable(typeof(ShipPhaseSummary))]
[JsonSerializable(typeof(DecomposePhaseSummary))]
internal partial class PhaseSummaryJsonContext : JsonSerializerContext { }
