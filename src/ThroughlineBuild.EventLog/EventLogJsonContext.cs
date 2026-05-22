using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.EventLog;

[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class EventLogJsonContext : JsonSerializerContext { }
