using System.Text.Json.Serialization;

namespace ThroughlineBuild.Scaffold;

[JsonSerializable(typeof(OpDoc))]
[JsonSerializable(typeof(DispatchEntry))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(Brief))]
[JsonSerializable(typeof(List<DispatchEntry>))]
[JsonSerializable(typeof(List<Plan>))]
[JsonSerializable(typeof(List<Brief>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<DispatchEntry>))]
[JsonSerializable(typeof(IReadOnlyList<Plan>))]
[JsonSerializable(typeof(IReadOnlyList<Brief>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class ScaffoldJsonContext : JsonSerializerContext { }
