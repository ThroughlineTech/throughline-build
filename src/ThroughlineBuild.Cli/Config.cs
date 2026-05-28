using Tomlyn;
using Tomlyn.Model;
using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

public record TicketingConfig(
    string BackendName,
    string PlaneBaseUrl,
    string PlaneWorkspaceSlug,
    string PlaneProjectId,
    string PlaneApiTokenEnv,
    string PlaneProjectIdentifier = "",
    string PlaneProjectName = "",
    string? PlaneApiToken = null);

public record LlmConfig(
    string DefaultModel,
    string AnthropicApiKeyEnv,
    string? AnthropicApiKey = null);

public record AgentConfig(string Executable, int? MaxOutputTokens);

public record WorkersConfig(
    string DefaultAgent,
    int TimeoutMinutes,
    IReadOnlyDictionary<string, AgentConfig> Agents,
    IReadOnlyDictionary<string, string> Phases);

public record EventsConfig(string LogDirectory);

public record ReviewConfig(
    int VerifierTimeoutMinutes,
    IReadOnlyList<string> VerifierAllowedTools,
    IReadOnlyList<CheckSpec> Checks);

public record ShipConfig(
    string Remote,
    string BaseBranch,
    bool DeleteFeatureBranch,
    IReadOnlyList<CheckSpec> RegressionChecks);

public record BuildConfig(
    TicketingConfig Ticketing,
    LlmConfig Llm,
    WorkersConfig Workers,
    EventsConfig Events,
    ReviewConfig Review,
    ShipConfig Ship,
    ProjectContext Project);

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
        var review = ReadReviewSection(root);
        var ship = ReadShipSection(root);
        var project = ReadProjectSection(root, path);

        return new BuildConfig(ticketing, llm, workers, events, review, ship, project);
    }

    public static string ResolveLogDirectory(string configFilePath, string rawLogDir, string cwdFallback)
    {
        if (Path.IsPathRooted(rawLogDir))
            return rawLogDir;
        var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(configFilePath)) ?? cwdFallback;
        return Path.Combine(projectRoot, rawLogDir);
    }

    public static BuildSecrets ResolveSecrets(BuildConfig config)
    {
        var planeToken = config.Ticketing.PlaneApiToken;
        if (string.IsNullOrEmpty(planeToken))
            planeToken = Environment.GetEnvironmentVariable(config.Ticketing.PlaneApiTokenEnv);
        if (string.IsNullOrEmpty(planeToken))
            throw new ConfigException(
                $"plane_api_token not set in config and required environment variable '{config.Ticketing.PlaneApiTokenEnv}' is not set");

        var anthropicKey = config.Llm.AnthropicApiKey;
        if (string.IsNullOrEmpty(anthropicKey))
            anthropicKey = Environment.GetEnvironmentVariable(config.Llm.AnthropicApiKeyEnv);

        return new BuildSecrets(planeToken, string.IsNullOrEmpty(anthropicKey) ? null : anthropicKey);
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

    private static IReadOnlyList<string> OptionalStringList(TomlTable section, string key, IReadOnlyList<string> defaultValue)
    {
        if (!section.TryGetValue(key, out var val) || val is not TomlArray arr)
            return defaultValue;
        var result = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is string s)
                result.Add(s);
        }
        return result.AsReadOnly();
    }

    private static TicketingConfig ReadTicketingSection(TomlTable root)
    {
        var t = RequireSection(root, "ticketing");
        return new TicketingConfig(
            BackendName: RequireString(t, "ticketing", "backend"),
            PlaneBaseUrl: RequireString(t, "ticketing", "plane_base_url"),
            PlaneWorkspaceSlug: RequireString(t, "ticketing", "plane_workspace_slug"),
            PlaneProjectId: RequireString(t, "ticketing", "plane_project_id"),
            PlaneApiTokenEnv: OptionalString(t, "plane_api_token_env", "PLANE_API_TOKEN"),
            PlaneProjectIdentifier: OptionalString(t, "plane_project_identifier", string.Empty),
            PlaneProjectName: OptionalString(t, "plane_project_name", string.Empty),
            PlaneApiToken: OptionalString(t, "plane_api_token", string.Empty) is var tok && tok.Length > 0 ? tok : null);
    }

    private static LlmConfig ReadLlmSection(TomlTable root)
    {
        if (!root.TryGetValue("llm", out var val) || val is not TomlTable t)
            return new LlmConfig(string.Empty, string.Empty);
        return new LlmConfig(
            DefaultModel: OptionalString(t, "default_model", string.Empty),
            AnthropicApiKeyEnv: OptionalString(t, "anthropic_api_key_env", string.Empty),
            AnthropicApiKey: OptionalString(t, "anthropic_api_key", string.Empty) is var key && key.Length > 0 ? key : null);
    }

    private static WorkersConfig ReadWorkersSection(TomlTable root)
    {
        var t = RequireSection(root, "workers");

        // Hard-break: old flat keys are not supported; throw a migration error.
        if (t.ContainsKey("claude_code_executable"))
            throw new ConfigException(
                "key 'claude_code_executable' in [workers] is no longer supported. " +
                "Move it to [workers.<agent-name>] as 'executable = ...' (e.g. [workers.claude-code]).");
        if (t.ContainsKey("max_output_tokens"))
            throw new ConfigException(
                "key 'max_output_tokens' in [workers] is no longer supported. " +
                "Move it to [workers.<agent-name>] as 'max_output_tokens = ...' (e.g. [workers.claude-code]).");

        var defaultAgent = RequireString(t, "workers", "default_agent");
        var timeoutMinutes = OptionalInt(t, "timeout_minutes", 30);

        // Enumerate sub-tables as agent configs; handle "phases" sub-table separately.
        var agents = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
        var phases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in t)
        {
            if (kv.Value is not TomlTable subTable)
                continue;

            if (kv.Key == "phases")
            {
                // Parse phases: each value must be one of the known phase keys.
                var knownPhases = new HashSet<string>(StringComparer.Ordinal) { "plan", "implement", "review" };
                foreach (var phaseKv in subTable)
                {
                    if (!knownPhases.Contains(phaseKv.Key))
                        throw new ConfigException(
                            $"unknown phase key '{phaseKv.Key}' in [workers.phases]; allowed keys are: plan, implement, review");
                    if (phaseKv.Value is not string agentName || string.IsNullOrEmpty(agentName))
                        throw new ConfigException(
                            $"value for '{phaseKv.Key}' in [workers.phases] must be a non-empty string");
                    phases[phaseKv.Key] = agentName;
                }
                continue;
            }

            var executable = RequireString(subTable, $"workers.{kv.Key}", "executable");
            int? maxOutputTokens = null;
            if (subTable.TryGetValue("max_output_tokens", out var motVal))
            {
                if (motVal is long l) maxOutputTokens = (int)l;
                else if (motVal is int i) maxOutputTokens = i;
            }
            agents[kv.Key] = new AgentConfig(executable, maxOutputTokens);
        }

        return new WorkersConfig(
            DefaultAgent: defaultAgent,
            TimeoutMinutes: timeoutMinutes,
            Agents: agents,
            Phases: phases);
    }

    private static EventsConfig ReadEventsSection(TomlTable root)
    {
        var t = RequireSection(root, "events");
        return new EventsConfig(
            LogDirectory: RequireString(t, "events", "log_directory"));
    }

    private static readonly IReadOnlyList<string> DefaultVerifierAllowedTools =
        new List<string> { "Read", "Grep", "Glob" }.AsReadOnly();

    private static ReviewConfig ReadReviewSection(TomlTable root)
    {
        if (!root.TryGetValue("review", out var val) || val is not TomlTable t)
        {
            return new ReviewConfig(
                VerifierTimeoutMinutes: 15,
                VerifierAllowedTools: DefaultVerifierAllowedTools,
                Checks: Array.Empty<CheckSpec>());
        }

        var timeoutMinutes = OptionalInt(t, "verifier_timeout_minutes", 15);
        var allowedTools = OptionalStringList(t, "verifier_allowed_tools", DefaultVerifierAllowedTools);

        var checks = new List<CheckSpec>();
        if (t.TryGetValue("checks", out var checksVal) && checksVal is TomlTableArray checksArr)
        {
            foreach (var entry in checksArr)
            {
                var name = RequireString(entry, "review.checks", "name");
                var executable = RequireString(entry, "review.checks", "executable");
                var arguments = OptionalStringList(entry, "arguments", Array.Empty<string>());
                var timeoutMins = OptionalInt(entry, "timeout_minutes", 5);
                checks.Add(new CheckSpec(
                    Name: name,
                    Executable: executable,
                    Arguments: arguments,
                    Timeout: TimeSpan.FromMinutes(timeoutMins)));
            }
        }

        return new ReviewConfig(
            VerifierTimeoutMinutes: timeoutMinutes,
            VerifierAllowedTools: allowedTools,
            Checks: checks.AsReadOnly());
    }

    private static ShipConfig ReadShipSection(TomlTable root)
    {
        if (!root.TryGetValue("ship", out var val) || val is not TomlTable t)
        {
            return new ShipConfig(
                Remote: "origin",
                BaseBranch: "main",
                DeleteFeatureBranch: true,
                RegressionChecks: Array.Empty<CheckSpec>());
        }

        var remote = OptionalString(t, "remote", "origin");
        var baseBranch = OptionalString(t, "base_branch", "main");
        var deleteFeatureBranch = true;
        if (t.TryGetValue("delete_feature_branch", out var dfbVal) && dfbVal is bool dfb)
            deleteFeatureBranch = dfb;

        var checks = new List<CheckSpec>();
        if (t.TryGetValue("regression_checks", out var checksVal) && checksVal is TomlTableArray checksArr)
        {
            foreach (var entry in checksArr)
            {
                var name = RequireString(entry, "ship.regression_checks", "name");
                var executable = RequireString(entry, "ship.regression_checks", "executable");
                var arguments = OptionalStringList(entry, "arguments", Array.Empty<string>());
                var timeoutMins = OptionalInt(entry, "timeout_minutes", 5);
                checks.Add(new CheckSpec(
                    Name: name,
                    Executable: executable,
                    Arguments: arguments,
                    Timeout: TimeSpan.FromMinutes(timeoutMins)));
            }
        }

        return new ShipConfig(
            Remote: remote,
            BaseBranch: baseBranch,
            DeleteFeatureBranch: deleteFeatureBranch,
            RegressionChecks: checks.AsReadOnly());
    }

    private static ProjectContext ReadProjectSection(TomlTable root, string configPath)
    {
        if (!root.TryGetValue("project", out var val) || val is not TomlTable t)
            return ProjectContext.Empty;

        var language = OptionalString(t, "language", string.Empty);
        var framework = OptionalString(t, "framework", string.Empty);
        var packageManager = OptionalString(t, "package_manager", string.Empty);
        var buildCommand = OptionalString(t, "build_command", string.Empty);
        var testCommand = OptionalString(t, "test_command", string.Empty);
        var installCommand = OptionalString(t, "install_command", string.Empty);
        var devCommand = OptionalString(t, "dev_command", string.Empty);
        var planeProjectUrl = OptionalString(t, "plane_project_url", string.Empty);
        var workflowTool = OptionalString(t, "workflow_tool", "build");

        // Validate workflow_tool value
        if (workflowTool != "build" && workflowTool != "claude-config")
            throw new ConfigException($"key 'workflow_tool' in [project] must be either \"build\" or \"claude-config\", got \"{workflowTool}\"");

        var notes = string.Empty;
        var notesFile = OptionalString(t, "notes_file", string.Empty);
        if (!string.IsNullOrEmpty(notesFile))
        {
            string resolved = Path.IsPathRooted(notesFile)
                ? notesFile
                : Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, notesFile);

            if (File.Exists(resolved))
            {
                try
                {
                    notes = File.ReadAllText(resolved);
                }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"Warning: project.notes_file '{resolved}' could not be read ({ex.Message}) - proceeding with empty Notes");
                }
            }
            else
            {
                Console.Error.WriteLine($"Warning: project.notes_file '{resolved}' not found - proceeding with empty Notes");
            }
        }

        return new ProjectContext(
            Language: language,
            Framework: framework,
            PackageManager: packageManager,
            BuildCommand: buildCommand,
            TestCommand: testCommand,
            InstallCommand: installCommand,
            DevCommand: devCommand,
            PlaneProjectUrl: planeProjectUrl,
            Notes: notes,
            WorkflowTool: workflowTool);
    }
}
