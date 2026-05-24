using System.Text.Json;

namespace ThroughlineBuild.Helpers;

public static class LlmUsageFlattener
{
    public static IReadOnlyDictionary<string, object>? FlattenLlmUsage(object usageObj)
    {
        var result = new Dictionary<string, object>();

        // Handle IDictionary path (from unit tests or direct construction)
        if (usageObj is IDictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Value is not null)
                {
                    result[kvp.Key] = UnwrapJsonElement(kvp.Value);
                }
            }
            return result;
        }

        // Handle JsonElement path (from envelope round-trip)
        if (usageObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
            {
                result[prop.Name] = UnwrapJsonElement(prop.Value);
            }
            return result;
        }

        // Neither dictionary nor JsonElement object - skip silently
        return null;
    }

    public static object UnwrapJsonElement(object value)
    {
        if (value is not JsonElement je)
            return value;

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? "",
            JsonValueKind.Number => je.TryGetInt32(out var intVal) ? intVal : je.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => (object?)null ?? "",
            _ => value // Return as-is for Object, Array, etc.
        };
    }
}
