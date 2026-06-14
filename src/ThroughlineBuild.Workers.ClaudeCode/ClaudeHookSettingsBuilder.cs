using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

public static class ClaudeHookSettingsBuilder
{
    public static string Build(string executablePath, string runDirectory, string runId,
        bool skipDangerousModePermissionPrompt = false)
        => Build([executablePath], runDirectory, runId, skipDangerousModePermissionPrompt);

    public static string Build(IReadOnlyList<string> commandPrefix, string runDirectory, string runId,
        bool skipDangerousModePermissionPrompt = false)
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
        // skipDangerousModePermissionPrompt narrowly suppresses claude's one-time "Bypass
        // Permissions mode" acceptance dialog - which --dangerously-skip-permissions does NOT
        // auto-accept and which otherwise hangs an unattended PTY launch. It is scoped to this
        // ephemeral per-run settings file; unlike IS_SANDBOX it does not alter claude's global
        // sandbox detection or tool-permission behavior. Omitted entirely when not requested.
        var settings = new ClaudeHookSettings(
            skipDangerousModePermissionPrompt ? true : null,
            new Dictionary<string, ClaudeHookMatcher[]>
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
    [property: JsonPropertyName("skipDangerousModePermissionPrompt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SkipDangerousModePermissionPrompt,
    [property: JsonPropertyName("hooks")] Dictionary<string, ClaudeHookMatcher[]> Hooks);
internal sealed record ClaudeHookMatcher(
    [property: JsonPropertyName("hooks")] ClaudeHookDefinition[] Hooks);
internal sealed record ClaudeHookDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("timeout")] int Timeout);
