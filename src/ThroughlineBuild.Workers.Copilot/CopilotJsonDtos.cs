using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.Copilot;

public record CopilotResultDebugDto(
    string Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason);

[JsonSerializable(typeof(CopilotResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class CopilotJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

[JsonSerializable(typeof(CopilotResultDebugDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class CopilotDebugCaptureJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
