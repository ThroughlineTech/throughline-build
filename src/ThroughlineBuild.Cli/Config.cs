using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.ClaudeCode;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

public record TicketingConfig(
    string BackendName,
    string PlaneBaseUrl,
    string PlaneWorkspaceSlug,
    string PlaneProjectId,
    string PlaneApiTokenEnv,
    string PlaneProjectIdentifier = "",
    string PlaneProjectName = "",
    string? PlaneApiToken = null,
    int PlaneRequestsPerMinute = PlaneClientOptions.DefaultRequestsPerMinute,
    // Path to a file whose entire trimmed contents are the Plane API token. Checked after
    // plane_api_token and plane_api_token_env; unlike either, it does not depend on which shell
    // (interactive or not) launched the process, so it is the resolution path that works the same
    // way for a human's terminal and for a non-interactive agent harness. See TLB-638.
    string PlaneApiTokenFile = "");

public record LlmConfig(
    string DefaultModel,
    string AnthropicApiKeyEnv,
    string? AnthropicApiKey = null);

public record AgentConfig(
    string Executable,
    int? MaxOutputTokens,
    IReadOnlyDictionary<WorkerSize, ModelTier> Sizes,
    bool BypassPermissions = true,
    ClaudeCodeTransport Transport = ClaudeCodeTransport.Print);

public record WorkersConfig(
    string DefaultAgent,
    int TimeoutMinutes,
    IReadOnlyDictionary<string, AgentConfig> Agents,
    IReadOnlyDictionary<string, string> Phases,
    int MaxConcurrency = 4);

public record EventsConfig(string LogDirectory);

public record ReviewConfig(
    int VerifierTimeoutMinutes,
    IReadOnlyList<string> VerifierAllowedTools,
    IReadOnlyList<CheckSpec> Checks,
    bool VerifyGateVacuity = true);

public record ShipConfig(
    string Remote,
    string BaseBranch,
    bool DeleteFeatureBranch,
    IReadOnlyList<CheckSpec> RegressionChecks,
    bool Push = true);

public record WorkConfig(string? TargetBranch);

public record WorktreeConfig(
    string Root,
    IReadOnlyList<string> SeedFiles)
{
    public static WorktreeConfig Default => new(".worktrees/conductor", Array.Empty<string>());
}

public record WavesConfig(
    int Cap,
    IReadOnlyList<WaveSerializeRule> Serialize)
{
    public static WavesConfig Default => new(WavePlanner.DefaultCap, Array.Empty<WaveSerializeRule>());
}

// [plan] section: controls whether chain planning runs a worker investigation or promotes in place.
// Standalone `build plan` ignores this setting unless --from-brief explicitly requests promotion.
public record PlanConfig(string Mode)
{
    public bool IsPromote => string.Equals(Mode, "promote", StringComparison.OrdinalIgnoreCase);
    public static PlanConfig Default => new PlanConfig("promote");
}

// [batch] section: caps that gate the batch-implement path. When any cap is exceeded the
// conductor falls back to the proven per-ticket chain and logs which cap triggered the fallback.
// All caps are checked before the batch session starts, using only declared ticket metadata
// (count, size score from S/M/L labels, total description bytes as a context proxy).
// Default values are intentionally conservative: they keep batch mode useful for the common
// cohesive group of 2-8 small-to-medium tickets while blocking oversized or context-heavy groups.
public record BatchConfig(
    // Maximum number of tickets allowed in a single batch session.
    int MaxTickets = 8,
    // Maximum aggregate size score (S=1, M=2, L=4). Caps the total change footprint.
    int MaxSizeScore = 16,
    // Maximum total bytes of ticket description HTML across all batch tickets.
    // Used as a proxy for the estimated worker context required to hold all plans at once.
    int MaxDescriptionBytes = 200_000)
{
    public static BatchConfig Default => new BatchConfig();
}

public record BuildConfig(
    TicketingConfig Ticketing,
    LlmConfig Llm,
    WorkersConfig Workers,
    EventsConfig Events,
    ReviewConfig Review,
    ShipConfig Ship,
    WorkConfig Work,
    WorktreeConfig Worktree,
    WavesConfig Waves,
    ProjectContext Project,
    PlanConfig Plan,
    BatchConfig Batch)
{
    public string ResolveTargetBranch() => Work.TargetBranch ?? Ship.BaseBranch;

    // True when the merge target comes from an explicit [work].target_branch override
    // rather than the [ship].base_branch default. Used to label the resolved target's
    // source in ship/chain progress so a silent fallback to base can never surprise.
    public bool TargetBranchOverridden => Work.TargetBranch is not null;
}

public record BuildSecrets(string PlaneApiToken, string? AnthropicApiKey);

public class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

public enum BuildConfigLoadMode
{
    Full,
    CandidateStandalone,
    WorktreeStandalone,
    GateStandalone,
    WavesStandalone,
    ProfileApply
}

public static class BuildConfigLoader
{
    // Bounded by the repository: the shared resolver stops at the worktree (reaching the main
    // worktree when the caller sits in a linked one), so a tree whose repository holds no
    // .build/config.toml fails closed instead of adopting an unrelated ancestor's config.
    public static string? FindConfigFile(string startDir) =>
        RepositoryLayout.Resolve(startDir).FindBuildDataFile("config.toml");

    public static BuildConfig Load(
        string path,
        Action<string>? warnSink = null,
        Func<string, bool>? branchExists = null,
        BuildConfigLoadMode mode = BuildConfigLoadMode.Full)
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
            root = TomlSerializer.Deserialize(
                content,
                BuildTomlSerializerContext.Default.TomlTable)
                ?? throw new ConfigException("TOML document produced no root table");
        }
        catch (Exception ex)
        {
            throw new ConfigException($"failed to parse TOML in '{path}': {ex.Message}");
        }

        var ticketing = mode == BuildConfigLoadMode.Full
            ? ReadTicketingSection(root)
            : EmptyTicketingConfig();
        if (mode == BuildConfigLoadMode.Full)
            ValidateTicketingFilled(ticketing, path);
        var llm = mode == BuildConfigLoadMode.Full
            ? ReadLlmSection(root)
            : new LlmConfig(string.Empty, string.Empty);
        var workers = mode == BuildConfigLoadMode.Full
            ? ReadWorkersSection(root)
            : EmptyWorkersConfig();
        var events = mode == BuildConfigLoadMode.Full
            ? ReadEventsSection(root)
            : EmptyEventsConfig();
        var review = mode is BuildConfigLoadMode.Full or BuildConfigLoadMode.GateStandalone
            ? ReadReviewSection(root)
            : EmptyReviewConfig();
        var ship = mode == BuildConfigLoadMode.Full
            ? ReadShipSection(root)
            : EmptyShipConfig();
        var work = mode == BuildConfigLoadMode.Full
            ? ReadWorkSection(root)
            : new WorkConfig(TargetBranch: null);
        var worktree = mode is BuildConfigLoadMode.Full or BuildConfigLoadMode.WorktreeStandalone
            ? ReadWorktreeSection(root)
            : WorktreeConfig.Default;
        var waves = mode is BuildConfigLoadMode.Full or BuildConfigLoadMode.WavesStandalone
            ? ReadWavesSection(root)
            : WavesConfig.Default;
        var project = mode switch
        {
            BuildConfigLoadMode.Full => ReadProjectSection(root, path),
            BuildConfigLoadMode.WorktreeStandalone => ReadProjectInstallCommand(root),
            _ => ProjectContext.Empty,
        };
        var plan = mode == BuildConfigLoadMode.Full
            ? ReadPlanSection(root)
            : PlanConfig.Default;
        var batch = mode == BuildConfigLoadMode.Full
            ? ReadBatchSection(root)
            : BatchConfig.Default;

        // Non-fatal unknown-key warning pass: emit one warning per unrecognized key.
        var warnings = new List<string>();
        CollectUnknownKeyWarnings(root, warnings);

        // Non-fatal target-branch validation: a hand-edited [work].target_branch bypasses
        // the `build settarget` branch check, so validate it here too. Warns (does not fail)
        // when the configured branch does not resolve to a local git ref, mirroring the
        // unknown-key warning channel. Skipped entirely when no validator is supplied
        // (e.g. unit tests, or commands that do not touch git).
        if (mode == BuildConfigLoadMode.Full &&
            branchExists is not null &&
            work.TargetBranch is not null &&
            !branchExists(work.TargetBranch))
            warnings.Add($"warning: [work].target_branch '{work.TargetBranch}' does not resolve to a local branch - ship will block until it exists or you run 'build settarget'");

        var emit = warnSink ?? (w => Console.Error.WriteLine(w));
        foreach (var warning in warnings)
            emit(warning);

        return new BuildConfig(ticketing, llm, workers, events, review, ship, work, worktree, waves, project, plan, batch);
    }

    private static TicketingConfig EmptyTicketingConfig() =>
        new(
            BackendName: string.Empty,
            PlaneBaseUrl: string.Empty,
            PlaneWorkspaceSlug: string.Empty,
            PlaneProjectId: string.Empty,
            PlaneApiTokenEnv: string.Empty);

    private static WorkersConfig EmptyWorkersConfig() =>
        new(
            DefaultAgent: string.Empty,
            TimeoutMinutes: 0,
            Agents: new Dictionary<string, AgentConfig>(StringComparer.Ordinal),
            Phases: new Dictionary<string, string>(StringComparer.Ordinal));

    private static EventsConfig EmptyEventsConfig() => new(string.Empty);

    private static ReviewConfig EmptyReviewConfig() =>
        new(
            VerifierTimeoutMinutes: 15,
            VerifierAllowedTools: DefaultVerifierAllowedTools,
            Checks: Array.Empty<CheckSpec>(),
            VerifyGateVacuity: true);

    private static ShipConfig EmptyShipConfig() =>
        new(
            Remote: "origin",
            BaseBranch: "main",
            DeleteFeatureBranch: true,
            RegressionChecks: Array.Empty<CheckSpec>(),
            Push: true);

    public static string ResolveLogDirectory(string configFilePath, string rawLogDir, string cwdFallback)
    {
        if (Path.IsPathRooted(rawLogDir))
            return rawLogDir;
        var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(configFilePath)) ?? cwdFallback;
        return Path.Combine(projectRoot, rawLogDir);
    }

    // Resolution order for the Plane API token: inline plane_api_token, then the environment
    // variable named by plane_api_token_env (so CI - which sets that variable directly, never via
    // an interactive shell rc file - is unchanged), then the file named by plane_api_token_file,
    // then failure. The file exists because env-var resolution silently depends on which shell
    // launched the process: bash only sources ~/.bashrc for interactive shells, zsh -c does not
    // read ~/.zshrc, bash -c does not read ~/.bash_profile, and an editor or agent harness launched
    // without a login shell inherits none of them. A file Build reads directly has no such
    // dependency on any shell. See TLB-638.
    //
    // configPath, when supplied, anchors a relative plane_api_token_file at the project root (the
    // parent of the directory holding config.toml), matching ResolveLogDirectory's convention.
    // warnSink receives the POSIX loose-permissions warning; it defaults to stderr like Load's.
    public static BuildSecrets ResolveSecrets(
        BuildConfig config, string? configPath = null, Action<string>? warnSink = null)
    {
        var emit = warnSink ?? (w => Console.Error.WriteLine(w));

        var planeToken = config.Ticketing.PlaneApiToken;
        var triedTokenFile = false;
        string? resolvedTokenFilePath = null;

        if (string.IsNullOrEmpty(planeToken))
            planeToken = Environment.GetEnvironmentVariable(config.Ticketing.PlaneApiTokenEnv);

        if (string.IsNullOrEmpty(planeToken) && !string.IsNullOrWhiteSpace(config.Ticketing.PlaneApiTokenFile))
        {
            triedTokenFile = true;
            resolvedTokenFilePath = ResolveTokenFilePath(config.Ticketing.PlaneApiTokenFile, configPath);
            if (File.Exists(resolvedTokenFilePath))
            {
                WarnIfTokenFileTooOpen(resolvedTokenFilePath, emit);
                var fileToken = File.ReadAllText(resolvedTokenFilePath).Trim();
                if (fileToken.Length > 0)
                    planeToken = fileToken;
            }
        }

        if (string.IsNullOrEmpty(planeToken))
            throw new ConfigException(BuildMissingPlaneTokenMessage(
                config.Ticketing, triedTokenFile, resolvedTokenFilePath));
        if (planeToken.StartsWith("REQUIRED_", StringComparison.Ordinal))
            throw new ConfigException(
                $"plane_api_token still holds the scaffold placeholder \"{planeToken}\"; replace it with your " +
                $"Plane API token, or set the '{config.Ticketing.PlaneApiTokenEnv}' environment variable.");

        var anthropicKey = config.Llm.AnthropicApiKey;
        if (string.IsNullOrEmpty(anthropicKey))
            anthropicKey = Environment.GetEnvironmentVariable(config.Llm.AnthropicApiKeyEnv);

        return new BuildSecrets(planeToken, string.IsNullOrEmpty(anthropicKey) ? null : anthropicKey);
    }

    // Names every source ResolveSecrets tried, so the diagnosis points at "no shell loaded this
    // token" rather than reading as a plain missing-config error. See TLB-638.
    private static string BuildMissingPlaneTokenMessage(
        TicketingConfig ticketing, bool triedTokenFile, string? resolvedTokenFilePath)
    {
        var fileClause = triedTokenFile
            ? $"token file '{resolvedTokenFilePath}' (from plane_api_token_file = \"{ticketing.PlaneApiTokenFile}\") was not found or was empty"
            : "plane_api_token_file is not set in config";
        return
            $"plane_api_token not set in config, environment variable '{ticketing.PlaneApiTokenEnv}' " +
            $"(from plane_api_token_env) is not set, and {fileClause}. Non-interactive shells - the " +
            "kind an agent harness, CI runner, or editor launched without a login shell uses - do not " +
            "source ~/.bashrc, ~/.zshrc, or ~/.bash_profile, so a token exported only there is invisible " +
            "here even though it works in your own terminal. Fix: set plane_api_token_file to a file " +
            "containing just the token, or export the token under plane_api_token_env's name in the " +
            "environment that actually launches build (not just an interactive shell's rc file).";
    }

    private static string ResolveTokenFilePath(string configuredPath, string? configPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;
        var root = configPath is not null
            ? Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();
        return Path.GetFullPath(configuredPath, root);
    }

    // POSIX-only advisory: a token file readable by group or other is a needless exposure of a live
    // secret. Warns and continues rather than failing the command - out of scope to enforce this,
    // and Windows ACLs have no group/other bits to compare against, so this never runs there. This
    // is the one deliberate, path-handling-only platform difference the readiness contract allows.
    private static void WarnIfTokenFileTooOpen(string path, Action<string> emit)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode openToOthers =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & openToOthers) == 0)
            return;
        emit($"warning: token file '{path}' is readable by group or other (mode {FormatUnixMode(mode)}); " +
             $"restrict it, e.g. chmod 600 \"{path}\"");
    }

    private static string FormatUnixMode(UnixFileMode mode)
    {
        int Digit(UnixFileMode read, UnixFileMode write, UnixFileMode execute) =>
            (mode.HasFlag(read) ? 4 : 0) + (mode.HasFlag(write) ? 2 : 0) + (mode.HasFlag(execute) ? 1 : 0);
        var owner = Digit(UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute);
        var group = Digit(UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute);
        var other = Digit(UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute);
        return $"{owner}{group}{other}";
    }

    private static readonly HashSet<string> KnownTopLevelSections = new(StringComparer.Ordinal)
    {
        "ticketing", "llm", "workers", "events", "review", "ship", "work", "worktree", "waves", "plan", "project", "batch"
    };

    private static readonly HashSet<string> KnownTicketingKeys = new(StringComparer.Ordinal)
    {
        "backend", "plane_base_url", "plane_workspace_slug", "plane_project_id",
        "plane_api_token_env", "plane_project_identifier", "plane_project_name", "plane_api_token",
        "plane_api_token_file", "plane_requests_per_minute"
    };

    private static readonly HashSet<string> KnownLlmKeys = new(StringComparer.Ordinal)
    {
        "default_model", "anthropic_api_key_env", "anthropic_api_key"
    };

    // Scalar keys recognized directly under [workers] (sub-tables are named agent configs or "phases").
    private static readonly HashSet<string> KnownWorkersScalarKeys = new(StringComparer.Ordinal)
    {
        "default_agent", "timeout_minutes", "max_concurrency"
    };

    private static readonly HashSet<string> KnownAgentKeys = new(StringComparer.Ordinal)
    {
        "executable", "max_output_tokens", "bypass_permissions", "sizes"
    };

    private static readonly HashSet<string> KnownSizesKeys = new(StringComparer.Ordinal)
    {
        "small", "medium", "large"
    };

    // Keys recognized inside an inline size tier table, e.g.
    // small = { model = "...", effort = "..." }. effort is optional.
    private static readonly HashSet<string> KnownTierKeys = new(StringComparer.Ordinal)
    {
        "model", "effort"
    };

    private static readonly HashSet<string> KnownEventsKeys = new(StringComparer.Ordinal)
    {
        "log_directory"
    };

    private static readonly HashSet<string> KnownReviewKeys = new(StringComparer.Ordinal)
    {
        "verifier_timeout_minutes", "verifier_allowed_tools", "checks", "verify_gate_vacuity"
    };

    private static readonly HashSet<string> KnownCheckEntryKeys = new(StringComparer.Ordinal)
    {
        "name", "executable", "arguments", "timeout_minutes", "role", "canary", "required_paths"
    };

    private static readonly HashSet<string> KnownShipKeys = new(StringComparer.Ordinal)
    {
        "remote", "base_branch", "delete_feature_branch", "regression_checks", "push"
    };

    private static readonly HashSet<string> KnownWorkKeys = new(StringComparer.Ordinal)
    {
        "target_branch"
    };

    private static readonly HashSet<string> KnownWorktreeKeys = new(StringComparer.Ordinal)
    {
        "root", "seed_files"
    };

    private static readonly HashSet<string> KnownWavesKeys = new(StringComparer.Ordinal)
    {
        "cap", "serialize"
    };

    private static readonly HashSet<string> KnownWavesSerializeKeys = new(StringComparer.Ordinal)
    {
        "kind", "paths"
    };

    private static readonly HashSet<string> KnownPlanKeys = new(StringComparer.Ordinal)
    {
        "mode"
    };

    private static readonly HashSet<string> KnownProjectKeys = new(StringComparer.Ordinal)
    {
        "language", "framework", "package_manager", "build_command", "test_command",
        "install_command", "dev_command", "plane_project_url", "workflow_tool", "notes_file",
        "convention_files", "preload_context", "context_hygiene"
    };

    private static readonly HashSet<string> KnownBatchKeys = new(StringComparer.Ordinal)
    {
        "max_tickets", "max_size_score", "max_description_bytes"
    };

    private static void CollectUnknownKeyWarnings(TomlTable root, List<string> warnings)
    {
        // Top-level sections
        foreach (var kv in root)
        {
            if (!KnownTopLevelSections.Contains(kv.Key))
                warnings.Add($"warning: unknown config key {kv.Key} - ignored");
        }

        // [ticketing]
        if (root.TryGetValue("ticketing", out var ticketingRaw) && ticketingRaw is TomlTable ticketing)
        {
            foreach (var kv in ticketing)
            {
                if (!KnownTicketingKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key ticketing.{kv.Key} - ignored");
            }
        }

        // [llm]
        if (root.TryGetValue("llm", out var llmRaw) && llmRaw is TomlTable llm)
        {
            foreach (var kv in llm)
            {
                if (!KnownLlmKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key llm.{kv.Key} - ignored");
            }
        }

        // [workers]
        if (root.TryGetValue("workers", out var workersRaw) && workersRaw is TomlTable workers)
        {
            foreach (var kv in workers)
            {
                if (kv.Value is TomlTable subTable)
                {
                    // [workers.phases] already hard-errors on unknown keys; do not double-warn.
                    if (kv.Key == "phases")
                        continue;

                    // Agent sub-table: check per-agent keys and sizes sub-table.
                    foreach (var agentKv in subTable)
                    {
                        if (agentKv.Key == "sizes" && agentKv.Value is TomlTable sizesTable)
                        {
                            foreach (var sizeKv in sizesTable)
                            {
                                if (!KnownSizesKeys.Contains(sizeKv.Key))
                                {
                                    warnings.Add($"warning: unknown config key workers.{kv.Key}.sizes.{sizeKv.Key} - ignored");
                                    continue;
                                }
                                // Recognized size key: warn on unknown keys inside its tier table.
                                if (sizeKv.Value is TomlTable tierTable)
                                {
                                    foreach (var tierKv in tierTable)
                                    {
                                        if (!KnownTierKeys.Contains(tierKv.Key))
                                            warnings.Add($"warning: unknown config key workers.{kv.Key}.sizes.{sizeKv.Key}.{tierKv.Key} - ignored");
                                    }
                                }
                            }
                        }
                        else if (!KnownAgentKeys.Contains(agentKv.Key)
                                 // `transport` is valid on any Claude-family agent (anything mapping to
                                 // ClaudeCodeAgent), not just the literal claude-code block; only warn
                                 // when it appears on gemini/codex/copilot.
                                 && !(kv.Key is not ("gemini" or "codex" or "copilot") && agentKv.Key == "transport"))
                        {
                            warnings.Add($"warning: unknown config key workers.{kv.Key}.{agentKv.Key} - ignored");
                        }
                    }
                }
                else if (!KnownWorkersScalarKeys.Contains(kv.Key))
                {
                    warnings.Add($"warning: unknown config key workers.{kv.Key} - ignored");
                }
            }
        }

        // [events]
        if (root.TryGetValue("events", out var eventsRaw) && eventsRaw is TomlTable events)
        {
            foreach (var kv in events)
            {
                if (!KnownEventsKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key events.{kv.Key} - ignored");
            }
        }

        // [review]
        if (root.TryGetValue("review", out var reviewRaw) && reviewRaw is TomlTable review)
        {
            foreach (var kv in review)
            {
                if (kv.Key == "checks")
                    continue;
                if (!KnownReviewKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key review.{kv.Key} - ignored");
            }
            if (review.TryGetValue("checks", out var checksRaw) && checksRaw is TomlTableArray checksArr)
            {
                foreach (var entry in checksArr)
                {
                    foreach (var kv in entry)
                    {
                        if (!KnownCheckEntryKeys.Contains(kv.Key))
                            warnings.Add($"warning: unknown config key review.checks.{kv.Key} - ignored");
                    }
                }
            }
        }

        // [ship]
        if (root.TryGetValue("ship", out var shipRaw) && shipRaw is TomlTable ship)
        {
            foreach (var kv in ship)
            {
                if (kv.Key == "regression_checks")
                    continue;
                if (!KnownShipKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key ship.{kv.Key} - ignored");
            }
            if (ship.TryGetValue("regression_checks", out var regrChecksRaw) && regrChecksRaw is TomlTableArray regrChecksArr)
            {
                foreach (var entry in regrChecksArr)
                {
                    foreach (var kv in entry)
                    {
                        if (!KnownCheckEntryKeys.Contains(kv.Key))
                            warnings.Add($"warning: unknown config key ship.regression_checks.{kv.Key} - ignored");
                    }
                }
            }
        }

        // [work]
        if (root.TryGetValue("work", out var workRaw) && workRaw is TomlTable work)
        {
            foreach (var kv in work)
            {
                if (!KnownWorkKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key work.{kv.Key} - ignored");
            }
        }

        // [worktree]
        if (root.TryGetValue("worktree", out var worktreeRaw) && worktreeRaw is TomlTable worktree)
        {
            foreach (var kv in worktree)
            {
                if (!KnownWorktreeKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key worktree.{kv.Key} - ignored");
            }
        }

        // [waves] and [[waves.serialize]]
        if (root.TryGetValue("waves", out var wavesRaw) && wavesRaw is TomlTable waves)
        {
            foreach (var kv in waves)
            {
                if (!KnownWavesKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key waves.{kv.Key} - ignored");
            }
            if (waves.TryGetValue("serialize", out var serializeRaw)
                && serializeRaw is TomlTableArray serialize)
            {
                foreach (var entry in serialize)
                {
                    foreach (var kv in entry)
                    {
                        if (!KnownWavesSerializeKeys.Contains(kv.Key))
                            warnings.Add($"warning: unknown config key waves.serialize.{kv.Key} - ignored");
                    }
                }
            }
        }

        // [plan]
        if (root.TryGetValue("plan", out var planRaw) && planRaw is TomlTable plan)
        {
            foreach (var kv in plan)
            {
                if (!KnownPlanKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key plan.{kv.Key} - ignored");
            }
        }

        // [project]
        if (root.TryGetValue("project", out var projectRaw) && projectRaw is TomlTable project)
        {
            foreach (var kv in project)
            {
                if (!KnownProjectKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key project.{kv.Key} - ignored");
            }
        }

        // [batch]
        if (root.TryGetValue("batch", out var batchRaw) && batchRaw is TomlTable batch)
        {
            foreach (var kv in batch)
            {
                if (!KnownBatchKeys.Contains(kv.Key))
                    warnings.Add($"warning: unknown config key batch.{kv.Key} - ignored");
            }
        }
    }

    private static TomlTable RequireSection(TomlTable root, string key)
    {
        if (!root.TryGetValue(key, out var val) || val is not TomlTable section)
            throw new ConfigException($"missing required TOML section [{key}]");
        return section;
    }

    private static CheckRole ParseCheckRole(TomlTable entry, string context)
    {
        var raw = OptionalString(entry, "role", "gating");
        return raw.ToLowerInvariant() switch
        {
            "gating" => CheckRole.Gating,
            "advisory" => CheckRole.Advisory,
            "setup" => CheckRole.Setup,
            _ => throw new ConfigException(
                $"key 'role' in [{context}] must be \"gating\", \"advisory\", or \"setup\", got \"{raw}\"")
        };
    }

    // Parses the optional inline-table array `canary = [{ path = "...", content = "..." }, ...]`.
    // Returns null when absent or empty; entries lacking a non-empty path are skipped (canary is
    // optional/best-effort). Every cast is guarded.
    private static IReadOnlyList<CanaryFile>? ParseCanary(TomlTable entry)
    {
        if (!entry.TryGetValue("canary", out var raw) || raw is not TomlArray arr || arr.Count == 0)
            return null;

        var files = new List<CanaryFile>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not TomlTable cf)
                continue;
            var path = cf.TryGetValue("path", out var pVal) && pVal is string p ? p : null;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var content = cf.TryGetValue("content", out var cVal) && cVal is string c ? c : "";
            files.Add(new CanaryFile(path.Trim(), content));
        }

        return files.Count > 0 ? files.AsReadOnly() : null;
    }

    private static IReadOnlyList<string>? ParseRequiredPaths(TomlTable entry)
    {
        var paths = OptionalStringList(entry, "required_paths", Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return paths.Count > 0 ? paths.AsReadOnly() : null;
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

    private static bool OptionalBool(TomlTable section, string key, bool defaultValue)
    {
        if (!section.TryGetValue(key, out var val) || val is not bool b)
            return defaultValue;
        return b;
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

    // 'build init' scaffolds the required Plane fields as REQUIRED_<NAME> placeholders the operator
    // must replace. RequireString only enforces non-empty, so an unreplaced placeholder loads cleanly
    // and then fails on the first API call as an opaque Plane 404 ("Page not found.") - which reads
    // like an outage or a wrong endpoint, not an unset config, and sends the operator down the wrong
    // path. Reject it here, before any HTTP, as an actionable Config error (exit 2) that names the
    // field and the file. Keyed on the documented REQUIRED_ convention so it never false-positives on
    // a real (or test) id like "abc-123"; a wrong-but-plausible id is left for the runtime 404, which
    // BuildProjectNotFoundMessage now explains.
    private static void ValidateTicketingFilled(TicketingConfig t, string configPath)
    {
        foreach (var (field, value) in new[]
        {
            ("plane_base_url", t.PlaneBaseUrl),
            ("plane_workspace_slug", t.PlaneWorkspaceSlug),
            ("plane_project_id", t.PlaneProjectId),
        })
        {
            if (value.StartsWith("REQUIRED_", StringComparison.Ordinal))
                throw new ConfigException(
                    $"{field} is not set in {configPath} - it still holds the scaffold placeholder " +
                    $"\"{value}\". Replace every REQUIRED_ placeholder with a real value: run `build init`, " +
                    $"or set {field} by hand (the project UUID is in Plane under Project Settings > General).");
        }
    }

    private static TicketingConfig ReadTicketingSection(TomlTable root)
    {
        var t = RequireSection(root, "ticketing");

        // A non-positive budget would surface as an ArgumentOutOfRangeException out of
        // RequestThrottle's constructor - an unhandled crash with no mention of config.
        // Reject it here as an actionable Config error instead.
        var rpm = OptionalInt(t, "plane_requests_per_minute", PlaneClientOptions.DefaultRequestsPerMinute);
        if (rpm <= 0)
            throw new ConfigException(
                $"plane_requests_per_minute must be a positive integer, got {rpm}. It is the per-process " +
                "cap on Plane API calls per minute; omit it to use the default of " +
                $"{PlaneClientOptions.DefaultRequestsPerMinute} (sized for Plane Cloud's 60/min limit).");

        return new TicketingConfig(
            BackendName: RequireString(t, "ticketing", "backend"),
            PlaneBaseUrl: RequireString(t, "ticketing", "plane_base_url"),
            PlaneWorkspaceSlug: RequireString(t, "ticketing", "plane_workspace_slug"),
            PlaneProjectId: RequireString(t, "ticketing", "plane_project_id"),
            PlaneApiTokenEnv: OptionalString(t, "plane_api_token_env", "PLANE_API_TOKEN"),
            PlaneProjectIdentifier: OptionalString(t, "plane_project_identifier", string.Empty),
            PlaneProjectName: OptionalString(t, "plane_project_name", string.Empty),
            PlaneApiToken: OptionalString(t, "plane_api_token", string.Empty) is var tok && tok.Length > 0 ? tok : null,
            PlaneRequestsPerMinute: rpm,
            PlaneApiTokenFile: OptionalString(t, "plane_api_token_file", string.Empty));
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
                var knownPhases = new HashSet<string>(StringComparer.Ordinal) { "plan", "implement", "review", "decompose" };
                foreach (var phaseKv in subTable)
                {
                    if (!knownPhases.Contains(phaseKv.Key))
                        throw new ConfigException(
                            $"unknown phase key '{phaseKv.Key}' in [workers.phases]; allowed keys are: plan, implement, review, decompose");
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

            // Per-agent bypass_permissions flag. Default true preserves the
            // historic behavior (CLI gets its unattended-mode flag); setting
            // false lets an operator opt back into the interactive permission
            // gate for a given agent.
            bool bypassPermissions = true;
            if (subTable.TryGetValue("bypass_permissions", out var bpVal) && bpVal is bool bp)
                bypassPermissions = bp;

            // Omitted-value default for a Claude-family agent's transport. Cross-platform operator
            // validation established interactive-hook as the load-bearing default: a Claude
            // block with no transport key now runs interactive-hook (requires Claude Code >= 2.1.177,
            // gated by ClaudeCodePreflight). Roll back to the legacy headless path with
            // transport = "print". Transport is honored for any agent name that maps to ClaudeCodeAgent
            // in WorkerAgentBuilder - i.e. anything that is not gemini/codex/copilot - so a custom-named
            // Claude block (e.g. [workers.my-claude]) can select print/interactive-hook too.
            var transport = ClaudeCodeTransport.InteractiveHook;
            bool isClaudeAgent = kv.Key is not ("gemini" or "codex" or "copilot");
            if (isClaudeAgent && subTable.TryGetValue("transport", out var transportValue))
            {
                if (transportValue is not string transportName)
                    throw new ConfigException(
                        $"[workers.{kv.Key}] transport must be a string: print or interactive-hook");
                transport = transportName switch
                {
                    "print" => ClaudeCodeTransport.Print,
                    "interactive-hook" => ClaudeCodeTransport.InteractiveHook,
                    _ => throw new ConfigException(
                        $"unknown [workers.{kv.Key}] transport '{transportName}'; supported values are: print, interactive-hook"),
                };
            }

            var sizes = new Dictionary<WorkerSize, ModelTier>();
            if (!subTable.TryGetValue("sizes", out var sizesVal) || sizesVal is not TomlTable sizesTable)
                throw new ConfigException(
                    $"missing required [workers.{kv.Key}.sizes] sub-table in config; " +
                    "must map small, medium, and large to model ids");
            var required = new[] { ("small", WorkerSize.Small), ("medium", WorkerSize.Medium), ("large", WorkerSize.Large) };
            var missing = new List<string>();
            foreach (var (key, ws) in required)
            {
                if (sizesTable.TryGetValue(key, out var mVal) && mVal is TomlTable tierTable)
                {
                    // Required, non-empty model string.
                    if (!tierTable.TryGetValue("model", out var modelVal) || modelVal is not string modelStr || string.IsNullOrEmpty(modelStr))
                        throw new ConfigException(
                            $"[workers.{kv.Key}.sizes.{key}] must be an inline table with a non-empty 'model' string (e.g. {key} = {{ model = \"...\" }})");

                    // Optional effort string; null when absent or empty.
                    string? effort = null;
                    if (tierTable.TryGetValue("effort", out var effortVal) && effortVal is string effortStr && !string.IsNullOrEmpty(effortStr))
                        effort = effortStr;

                    // claude-code only: reject model values the CLI cannot resolve (e.g. the
                    // "fable" alias trap) at config load, instead of letting them fail as an
                    // opaque is_error envelope deep inside a chain run.
                    if (kv.Key == "claude-code" && ClaudeCodeModelValidator.Validate(modelStr) is string modelError)
                        throw new ConfigException(
                            $"[workers.{kv.Key}.sizes.{key}] model: {modelError}");

                    sizes[ws] = new ModelTier(modelStr, effort);
                }
                else if (sizesTable.ContainsKey(key))
                {
                    // Present but not a table (bare string or other scalar): the
                    // pre-release schema dropped the bare-string form entirely.
                    throw new ConfigException(
                        $"[workers.{kv.Key}.sizes.{key}] must be an inline table like {{ model = \"...\", effort = \"...\" }}, not a bare string");
                }
                else
                {
                    missing.Add(key);
                }
            }
            if (missing.Count > 0)
                throw new ConfigException(
                    $"[workers.{kv.Key}.sizes] is missing required size keys: {string.Join(", ", missing)}");
            agents[kv.Key] = new AgentConfig(executable, maxOutputTokens, sizes.AsReadOnly(), bypassPermissions, transport);
        }

        // Validate that default_agent and every phase agent resolve to a defined
        // [workers.<name>] sub-table. Without this, a default_agent (or phase agent)
        // pointing at a missing/commented-out sub-table sails through Load() and only
        // blows up later at agent-resolution time as an *unhandled* exception
        // (Program.cs throws ConfigException outside any try/catch when the agent is
        // looked up). The classic trigger is leaving the [workers.codex] sections
        // commented in config.toml while default_agent = "codex". Catching it here
        // routes through the friendly "Config error:" handler (exit 2) and tells the
        // operator exactly how to fix it.
        if (!agents.ContainsKey(defaultAgent))
            throw new ConfigException(BuildUndefinedAgentMessage("default_agent", defaultAgent, agents.Keys));
        foreach (var phaseKv in phases)
        {
            if (!agents.ContainsKey(phaseKv.Value))
                throw new ConfigException(
                    BuildUndefinedAgentMessage($"[workers.phases].{phaseKv.Key}", phaseKv.Value, agents.Keys));
        }

        var maxConcurrency = OptionalInt(t, "max_concurrency", Math.Min(Environment.ProcessorCount, 4));

        return new WorkersConfig(
            DefaultAgent: defaultAgent,
            TimeoutMinutes: timeoutMinutes,
            Agents: agents,
            Phases: phases,
            MaxConcurrency: maxConcurrency);
    }

    // Build an actionable error for a setting (default_agent or a phase) that names an
    // agent with no matching [workers.<name>] sub-table - the common "codex sections
    // left commented out" case. Names the offending setting, the fix (uncomment/add the
    // sub-table or repoint the setting), and which agents ARE defined.
    private static string BuildUndefinedAgentMessage(string settingName, string agentName, ICollection<string> definedAgents)
    {
        var definedNames = new List<string>(definedAgents);
        definedNames.Sort(StringComparer.Ordinal);
        var available = definedNames.Count > 0
            ? $"Configured agents: {string.Join(", ", definedNames)}."
            : "No [workers.<name>] sub-tables are currently defined.";
        return
            $"{settingName} = \"{agentName}\" but there is no [workers.{agentName}] sub-table in config. " +
            $"Uncomment or add the [workers.{agentName}] and [workers.{agentName}.sizes] sections in " +
            $".build/config.toml, or set {settingName} to a configured agent. {available}";
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
                Checks: Array.Empty<CheckSpec>(),
                VerifyGateVacuity: true);
        }

        var timeoutMinutes = OptionalInt(t, "verifier_timeout_minutes", 15);
        var allowedTools = OptionalStringList(t, "verifier_allowed_tools", DefaultVerifierAllowedTools);
        var verifyGateVacuity = t.TryGetValue("verify_gate_vacuity", out var vgv) && vgv is bool vgvb ? vgvb : true;

        var checks = new List<CheckSpec>();
        if (t.TryGetValue("checks", out var checksVal) && checksVal is TomlTableArray checksArr)
        {
            foreach (var entry in checksArr)
            {
                var name = RequireString(entry, "review.checks", "name");
                var executable = RequireString(entry, "review.checks", "executable");
                var arguments = OptionalStringList(entry, "arguments", Array.Empty<string>());
                var timeoutMins = OptionalInt(entry, "timeout_minutes", 5);
                var role = ParseCheckRole(entry, "review.checks");
                var canary = ParseCanary(entry);
                var requiredPaths = ParseRequiredPaths(entry);
                checks.Add(new CheckSpec(
                    Name: name,
                    Executable: executable,
                    Arguments: arguments,
                    Timeout: TimeSpan.FromMinutes(timeoutMins),
                    Role: role,
                    Canary: canary,
                    RequiredPaths: requiredPaths));
            }
        }

        return new ReviewConfig(
            VerifierTimeoutMinutes: timeoutMinutes,
            VerifierAllowedTools: allowedTools,
            Checks: checks.AsReadOnly(),
            VerifyGateVacuity: verifyGateVacuity);
    }

    private static ShipConfig ReadShipSection(TomlTable root)
    {
        if (!root.TryGetValue("ship", out var val) || val is not TomlTable t)
        {
            return new ShipConfig(
                Remote: "origin",
                BaseBranch: "main",
                DeleteFeatureBranch: true,
                RegressionChecks: Array.Empty<CheckSpec>(),
                Push: true);
        }

        var remote = OptionalString(t, "remote", "origin");
        var baseBranch = OptionalString(t, "base_branch", "main");
        var deleteFeatureBranch = true;
        if (t.TryGetValue("delete_feature_branch", out var dfbVal) && dfbVal is bool dfb)
            deleteFeatureBranch = dfb;
        var push = true;
        if (t.TryGetValue("push", out var pushVal) && pushVal is bool p)
            push = p;

        var checks = new List<CheckSpec>();
        if (t.TryGetValue("regression_checks", out var checksVal) && checksVal is TomlTableArray checksArr)
        {
            foreach (var entry in checksArr)
            {
                var name = RequireString(entry, "ship.regression_checks", "name");
                var executable = RequireString(entry, "ship.regression_checks", "executable");
                var arguments = OptionalStringList(entry, "arguments", Array.Empty<string>());
                var timeoutMins = OptionalInt(entry, "timeout_minutes", 5);
                var role = ParseCheckRole(entry, "ship.regression_checks");
                var canary = ParseCanary(entry);
                var requiredPaths = ParseRequiredPaths(entry);
                checks.Add(new CheckSpec(
                    Name: name,
                    Executable: executable,
                    Arguments: arguments,
                    Timeout: TimeSpan.FromMinutes(timeoutMins),
                    Role: role,
                    Canary: canary,
                    RequiredPaths: requiredPaths));
            }
        }

        return new ShipConfig(
            Remote: remote,
            BaseBranch: baseBranch,
            DeleteFeatureBranch: deleteFeatureBranch,
            RegressionChecks: checks.AsReadOnly(),
            Push: push);
    }

    private static WorkConfig ReadWorkSection(TomlTable root)
    {
        if (!root.TryGetValue("work", out var val) || val is not TomlTable t)
            return new WorkConfig(TargetBranch: null);
        string? targetBranch = null;
        if (t.TryGetValue("target_branch", out var tbVal) && tbVal is string tbStr && !string.IsNullOrEmpty(tbStr))
            targetBranch = tbStr;
        return new WorkConfig(TargetBranch: targetBranch);
    }

    private static WorktreeConfig ReadWorktreeSection(TomlTable root)
    {
        if (!root.TryGetValue("worktree", out var val) || val is not TomlTable t)
            return WorktreeConfig.Default;

        var configuredRoot = OptionalString(t, "root", WorktreeConfig.Default.Root);
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new ConfigException("key 'root' in [worktree] must not be blank");
        var seeds = OptionalStringList(t, "seed_files", Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList()
            .AsReadOnly();
        return new WorktreeConfig(configuredRoot, seeds);
    }

    private static WavesConfig ReadWavesSection(TomlTable root)
    {
        if (!root.TryGetValue("waves", out var val) || val is not TomlTable table)
            return WavesConfig.Default;

        var cap = WavePlanner.DefaultCap;
        if (table.TryGetValue("cap", out var capValue))
        {
            cap = capValue switch
            {
                int intValue => intValue,
                long longValue when longValue is >= int.MinValue and <= int.MaxValue =>
                    (int)longValue,
                _ => throw new ConfigException(
                    "key 'cap' in [waves] must be an integer"),
            };
        }
        if (cap is < 1 or > WavePlanner.MaximumCap)
            throw new ConfigException(
                $"key 'cap' in [waves] must be an integer from 1 to {WavePlanner.MaximumCap}");

        var rules = new List<WaveSerializeRule>();
        if (table.TryGetValue("serialize", out var serializeValue))
        {
            if (serializeValue is not TomlTableArray serialize)
                throw new ConfigException(
                    "key 'serialize' in [waves] must be an array of tables");
            foreach (var entry in serialize)
            {
                var rawKind = RequireString(entry, "waves.serialize", "kind");
                var kind = rawKind switch
                {
                    "global" => WaveSerializeKind.Global,
                    "cohesive-module" => WaveSerializeKind.CohesiveModule,
                    "pairwise" => WaveSerializeKind.Pairwise,
                    _ => throw new ConfigException(
                        $"key 'kind' in [[waves.serialize]] must be \"global\", " +
                        $"\"cohesive-module\", or \"pairwise\", got \"{rawKind}\""),
                };
                var paths = OptionalStringList(entry, "paths", Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim())
                    .ToList();
                if (paths.Count == 0)
                    throw new ConfigException(
                        $"key 'paths' in [[waves.serialize]] for kind \"{rawKind}\" " +
                        "must contain at least one non-empty string");
                try
                {
                    rules.Add(new WaveSerializeRule(
                        kind,
                        paths.Select(WavePlanner.NormalizePathPattern).ToList()));
                }
                catch (ArgumentException ex)
                {
                    throw new ConfigException(
                        $"invalid path in [[waves.serialize]] for kind \"{rawKind}\": {ex.Message}");
                }
            }
        }
        return new WavesConfig(cap, rules.AsReadOnly());
    }

    private static PlanConfig ReadPlanSection(TomlTable root)
    {
        if (!root.TryGetValue("plan", out var val) || val is not TomlTable t)
            return PlanConfig.Default;

        var mode = OptionalString(t, "mode", "promote");
        if (!string.Equals(mode, "investigate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "promote", StringComparison.OrdinalIgnoreCase))
            throw new ConfigException(
                $"key 'mode' in [plan] must be either \"investigate\" or \"promote\", got \"{mode}\"");

        return new PlanConfig(mode);
    }

    private static BatchConfig ReadBatchSection(TomlTable root)
    {
        if (!root.TryGetValue("batch", out var val) || val is not TomlTable t)
            return BatchConfig.Default;

        var maxTickets = OptionalInt(t, "max_tickets", BatchConfig.Default.MaxTickets);
        var maxSizeScore = OptionalInt(t, "max_size_score", BatchConfig.Default.MaxSizeScore);
        var maxDescriptionBytes = OptionalInt(t, "max_description_bytes", BatchConfig.Default.MaxDescriptionBytes);

        if (maxTickets < 1)
            throw new ConfigException("key 'max_tickets' in [batch] must be a positive integer");
        if (maxSizeScore < 1)
            throw new ConfigException("key 'max_size_score' in [batch] must be a positive integer");
        if (maxDescriptionBytes < 1)
            throw new ConfigException("key 'max_description_bytes' in [batch] must be a positive integer");

        return new BatchConfig(maxTickets, maxSizeScore, maxDescriptionBytes);
    }

    private static ProjectContext ReadProjectInstallCommand(TomlTable root)
    {
        if (!root.TryGetValue("project", out var val) || val is not TomlTable t)
            return ProjectContext.Empty;

        return ProjectContext.Empty with
        {
            InstallCommand = OptionalString(t, "install_command", string.Empty)
        };
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

        // Project-convention bundle (derived; the deriver chose these paths). Read as a plain list of
        // relative paths; their CONTENTS are read lazily at brief-build from the live worktree, not
        // here (the target source may not exist yet at config load). Blank entries are dropped.
        var conventionFiles = OptionalStringList(t, "convention_files", Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList()
            .AsReadOnly();

        // Pre-load gate: default ON; set false to ablate the lever (restore the pre-preload brief).
        var preloadContext = OptionalBool(t, "preload_context", true);

        // Context-hygiene gate: default OFF (opt-in). Enables effort-gated lean planning for S briefs.
        var contextHygiene = OptionalBool(t, "context_hygiene", false);

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
            WorkflowTool: workflowTool)
        {
            ConventionFiles = conventionFiles,
            PreloadContext = preloadContext,
            ContextHygiene = contextHygiene
        };
    }
}
