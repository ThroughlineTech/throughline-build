using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

public record TicketingConfig(
    string BackendName,
    string PlaneBaseUrl,
    string PlaneWorkspaceSlug,
    string PlaneProjectId,
    string PlaneApiTokenEnv);

public record LlmConfig(
    string DefaultModel,
    string AnthropicApiKeyEnv);

public record WorkersConfig(
    string DefaultAgent,
    string ClaudeCodeExecutable,
    int TimeoutMinutes);

public record EventsConfig(string LogDirectory);

public record BuildConfig(
    TicketingConfig Ticketing,
    LlmConfig Llm,
    WorkersConfig Workers,
    EventsConfig Events);

public record BuildSecrets(string PlaneApiToken, string? AnthropicApiKey);

public class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

public static class BuildConfigLoader
{
    public static string? FindConfigFile(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, ".build", "config.toml");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        return null;
    }

    public static BuildConfig Load(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"failed to read config file '{path}': {ex.Message}");
        }

        TomlTable root;
        try
        {
            root = Toml.ToModel(content);
        }
        catch (Exception ex)
        {
            throw new ConfigException($"failed to parse TOML in '{path}': {ex.Message}");
        }

        var ticketing = ReadTicketingSection(root);
        var llm = ReadLlmSection(root);
        var workers = ReadWorkersSection(root);
        var events = ReadEventsSection(root);

        return new BuildConfig(ticketing, llm, workers, events);
    }

    public static BuildSecrets ResolveSecrets(BuildConfig config)
    {
        var planeToken = Environment.GetEnvironmentVariable(config.Ticketing.PlaneApiTokenEnv);
        if (string.IsNullOrEmpty(planeToken))
            throw new ConfigException(
                $"required environment variable '{config.Ticketing.PlaneApiTokenEnv}' is not set");

        var anthropicKey = Environment.GetEnvironmentVariable(config.Llm.AnthropicApiKeyEnv);

        return new BuildSecrets(planeToken, anthropicKey);
    }

    private static TomlTable RequireSection(TomlTable root, string key)
    {
        if (!root.TryGetValue(key, out var val) || val is not TomlTable section)
            throw new ConfigException($"missing required TOML section [{key}]");
        return section;
    }

    private static string RequireString(TomlTable section, string sectionName, string key)
    {
        if (!section.TryGetValue(key, out var val))
            throw new ConfigException($"missing required key '{key}' in [{sectionName}]");
        if (val is not string s || string.IsNullOrEmpty(s))
            throw new ConfigException($"key '{key}' in [{sectionName}] must be a non-empty string");
        return s;
    }

    private static string OptionalString(TomlTable section, string key, string defaultValue)
    {
        if (!section.TryGetValue(key, out var val) || val is not string s)
            return defaultValue;
        return s;
    }

    private static int OptionalInt(TomlTable section, string key, int defaultValue)
    {
        if (!section.TryGetValue(key, out var val))
            return defaultValue;
        if (val is long l) return (int)l;
        if (val is int i) return i;
        return defaultValue;
    }

    private static TicketingConfig ReadTicketingSection(TomlTable root)
    {
        var t = RequireSection(root, "ticketing");
        return new TicketingConfig(
            BackendName: RequireString(t, "ticketing", "backend"),
            PlaneBaseUrl: RequireString(t, "ticketing", "plane_base_url"),
            PlaneWorkspaceSlug: RequireString(t, "ticketing", "plane_workspace_slug"),
            PlaneProjectId: RequireString(t, "ticketing", "plane_project_id"),
            PlaneApiTokenEnv: RequireString(t, "ticketing", "plane_api_token_env"));
    }

    private static LlmConfig ReadLlmSection(TomlTable root)
    {
        if (!root.TryGetValue("llm", out var val) || val is not TomlTable t)
            return new LlmConfig(string.Empty, string.Empty);
        return new LlmConfig(
            DefaultModel: OptionalString(t, "default_model", string.Empty),
            AnthropicApiKeyEnv: OptionalString(t, "anthropic_api_key_env", string.Empty));
    }

    private static WorkersConfig ReadWorkersSection(TomlTable root)
    {
        var t = RequireSection(root, "workers");
        return new WorkersConfig(
            DefaultAgent: RequireString(t, "workers", "default_agent"),
            ClaudeCodeExecutable: RequireString(t, "workers", "claude_code_executable"),
            TimeoutMinutes: OptionalInt(t, "timeout_minutes", 30));
    }

    private static EventsConfig ReadEventsSection(TomlTable root)
    {
        var t = RequireSection(root, "events");
        return new EventsConfig(
            LogDirectory: RequireString(t, "events", "log_directory"));
    }
}
