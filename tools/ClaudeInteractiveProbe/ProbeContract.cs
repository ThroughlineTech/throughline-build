using System.Text.Json;

namespace ClaudeInteractiveProbe;

public sealed record StopPayloadShape(
    bool SessionId,
    bool Cwd,
    bool TranscriptPath,
    bool LastAssistantMessage,
    bool StopHookActive);

public static class ProbeContract
{
    public static StopPayloadShape InspectStopPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new StopPayloadShape(
            HasProperty(root, "session_id"),
            HasProperty(root, "cwd"),
            HasProperty(root, "transcript_path"),
            HasProperty(root, "last_assistant_message"),
            HasProperty(root, "stop_hook_active"));
    }

    public static string BuildSettingsJson(string hookCommand) => JsonSerializer.Serialize(new
    {
        hooks = new
        {
            Stop = new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new { type = "command", command = hookCommand, timeout = 30 }
                    }
                }
            }
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string QuoteCommandArgument(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "-._:/\\".Contains(c))
            ? value
            : $"\"{value.Replace("\"", "\\\"")}\"";

    public static string NormalizeHookPath(string path) => path.Replace('\\', '/');

    private static bool HasProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null;
}
