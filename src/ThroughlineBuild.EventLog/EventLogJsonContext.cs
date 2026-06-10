using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(EventLineDto))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Per-check evidence payloads (ship baseline_computed / baseline_recheck events).
[JsonSerializable(typeof(List<Dictionary<string, object>>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<long>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class EventLogJsonContext : JsonSerializerContext { }
