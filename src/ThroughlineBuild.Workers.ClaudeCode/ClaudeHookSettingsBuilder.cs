using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

public static class ClaudeHookSettingsBuilder
{
    public static string Build(string executablePath, string runDirectory, string runId)
    {
        var command = string.Join(" ", new[]
        {
            Quote(executablePath),
            "internal",
            "claude-stop-hook",
            "--run-dir",
            Quote(runDirectory),
            "--run-id",
            Quote(runId)
        });
        var settings = new ClaudeHookSettings(new Dictionary<string, ClaudeHookMatcher[]>
        {
            ["Stop"] = [new ClaudeHookMatcher([new ClaudeHookDefinition("command", command, 30)])]
        });
        return JsonSerializer.Serialize(settings, ClaudeHookJsonContext.Default.ClaudeHookSettings);
    }

    internal static string Quote(string value)
    {
        var normalized = value.Replace('\\', '/');
        return $"'{normalized.Replace("'", "'\"'\"'")}'";
    }
}

internal sealed record ClaudeHookSettings(
    [property: JsonPropertyName("hooks")] Dictionary<string, ClaudeHookMatcher[]> Hooks);
internal sealed record ClaudeHookMatcher(
    [property: JsonPropertyName("hooks")] ClaudeHookDefinition[] Hooks);
internal sealed record ClaudeHookDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("timeout")] int Timeout);
