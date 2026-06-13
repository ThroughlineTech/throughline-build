using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

public static class ClaudeHookSettingsBuilder
{
    public static string Build(string executablePath, string runDirectory, string runId)
        => Build([executablePath], runDirectory, runId);

    public static string Build(IReadOnlyList<string> commandPrefix, string runDirectory, string runId)
    {
        ArgumentNullException.ThrowIfNull(commandPrefix);
        if (commandPrefix.Count == 0 || commandPrefix.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Hook command prefix must contain non-empty arguments.", nameof(commandPrefix));

        var command = string.Join(" ", commandPrefix.Select(Quote).Concat(new[]
        {
            "internal",
            "claude-stop-hook",
            "--run-dir",
            Quote(runDirectory),
            "--run-id",
            Quote(runId)
        }));
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
