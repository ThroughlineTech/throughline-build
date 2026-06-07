using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.Codex;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Implements the 'build init' verb: writes .build/config.toml from the embedded template.
/// Not an ITicketCommand - init bootstraps the config before any config is present.
///
/// Two modes:
/// - Offline (no project name): writes the template with whatever values were supplied and
///   asks the operator to fill in REQUIRED fields manually. Behavior unchanged from before.
/// - Connected (project name + credentials): resolves or creates the Plane project, substitutes
///   the resolved id, runs SetupCommand provisioning (git init, .gitignore, states, labels),
///   verifies connectivity, and prints a one-line summary. The operator never types a project UUID.
/// </summary>
public static class InitCommand
{
    /// <summary>
    /// Execute the init command asynchronously.
    /// </summary>
    /// <param name="cwd">The working directory in which to create .build/config.toml.</param>
    /// <param name="force">When true, overwrite an existing config file.</param>
    /// <param name="printTemplate">When true, print the template to stdout and exit; do not write a file.</param>
    /// <param name="console">Console abstraction for output.</param>
    /// <param name="planeUrl">Optional: replaces REQUIRED_PLANE_BASE_URL in the template.</param>
    /// <param name="workspace">Optional: replaces REQUIRED_PLANE_WORKSPACE_SLUG in the template.</param>
    /// <param name="projectId">Optional: replaces REQUIRED_PLANE_PROJECT_ID in the template.</param>
    /// <param name="projectName">
    /// Optional: when set together with complete credentials (planeUrl, workspace, token), triggers
    /// connected mode. The project name is resolved to a Plane project id (finding or creating the
    /// project as needed), the id is substituted into the config, setup provisioning is run, and
    /// a connectivity summary is printed.
    /// </param>
    /// <param name="token">Optional: replaces REQUIRED_PLANE_API_TOKEN in the template (literal token value).</param>
    /// <param name="tokenEnv">Optional: if set, the plane_api_token line is replaced with plane_api_token_env = "VALUE".</param>
    /// <param name="fromFile">
    /// Optional path to a credentials input file. Lines have the form key = "value" or
    /// key = value with the same key names as the config [ticketing] section. Comment lines
    /// and blank lines are tolerated. Explicit flag values take precedence over file values.
    /// When null and stdin is redirected, stdin is read as a credentials file instead.
    /// </param>
    /// <param name="probeCodex">
    /// Optional injected Codex probe. When provided AND the target is being written (not
    /// --print-template, past the clobber guard), init queries Codex and rewrites the
    /// commented [workers.codex.sizes] block with a best-guess small/medium/large mapping plus
    /// a discovered-menu comment. On probe failure it leaves the static template block and
    /// prints one actionable warning, still exiting 0. The Claude block is never touched.
    /// null (the default) means no enrichment and no warning - keeps unit tests offline.
    /// </param>
    /// <param name="resolverOverride">
    /// Injection point for unit tests. When non-null, used instead of creating a real
    /// ProjectResolver from the supplied credentials.
    /// </param>
    /// <param name="setupFactory">
    /// Injection point for unit tests. When non-null, called with the resolved projectId and
    /// returns (ITicketingProvisioner, ITicketingConnectivity) instead of building a real
    /// PlaneTicketingClient.
    /// </param>
    /// <param name="localRepoOverride">
    /// Injection point for unit tests. When non-null, used instead of FileSystemLocalRepoOps.
    /// Only applies in connected mode (setup provisioning).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static async Task<int> ExecuteAsync(
        string cwd,
        bool force,
        bool printTemplate,
        IConsole console,
        string? planeUrl = null,
        string? workspace = null,
        string? projectId = null,
        string? projectName = null,
        string? token = null,
        string? tokenEnv = null,
        string? fromFile = null,
        Func<CodexProbeResult>? probeCodex = null,
        IProjectResolver? resolverOverride = null,
        Func<string, (ITicketingProvisioner, ITicketingConnectivity)>? setupFactory = null,
        ILocalRepoOps? localRepoOverride = null,
        CancellationToken ct = default)
    {
        var template = ConfigTemplateLoader.Load();

        // Load creds from --from file or from redirected stdin; explicit flags take precedence.
        if (fromFile is not null)
        {
            if (!File.Exists(fromFile))
            {
                console.ErrorWriteLine($"Error: credentials file not found: {fromFile}");
                return 1;
            }
            var fileContent = File.ReadAllText(fromFile, System.Text.Encoding.UTF8);
            ApplyCredsToParams(CredsFileParser.Parse(fileContent), ref planeUrl, ref workspace, ref projectId, ref projectName, ref token);
        }
        else if (console.IsInputRedirected)
        {
            var stdinContent = ReadAllLines(console);
            if (stdinContent.Length > 0)
                ApplyCredsToParams(CredsFileParser.Parse(stdinContent), ref planeUrl, ref workspace, ref projectId, ref projectName, ref token);
        }

        // Prompt for any remaining null values only when stdin is a TTY and no creds file path
        // was given (even a partial file is a signal that the operator is in non-interactive mode).
        if (!console.IsInputRedirected && fromFile is null)
            PromptForMissingValues(console, ref planeUrl, ref workspace, ref projectId, ref token);

        var content = ApplyFlags(template, planeUrl, workspace, projectId, token, tokenEnv);

        // --print-template NEVER probes: it stays offline-safe even when a probe is injected.
        if (printTemplate)
        {
            console.Write(content);
            return 0;
        }

        var target = Path.Combine(cwd, ".build", "config.toml");

        // Clobber guard runs BEFORE probing so the no-op path never spawns codex.
        if (File.Exists(target) && !force)
        {
            console.ErrorWriteLine($"Error: {target} already exists. Use --force to overwrite.");
            return 1;
        }

        // Detect connected mode: project name provided, no explicit project id, and full credentials
        // are available. An explicit projectId (from --project-id or the creds file's plane_project_id
        // key) takes precedence over resolution by name: if the caller already knows the id, we skip
        // the network round-trip and stay offline.
        // The effective token is the literal token flag value; if only --token-env is given,
        // we read that env var so the resolver can make actual API calls.
        var effectiveToken = token ?? (tokenEnv is not null ? Environment.GetEnvironmentVariable(tokenEnv) : null);
        var isConnected = !string.IsNullOrEmpty(projectName)
            && string.IsNullOrEmpty(projectId)
            && !string.IsNullOrEmpty(planeUrl)
            && !string.IsNullOrEmpty(workspace)
            && !string.IsNullOrEmpty(effectiveToken);

        if (isConnected)
        {
            return await RunConnectedAsync(
                cwd, content, target, console, probeCodex,
                planeUrl!, workspace!, effectiveToken!, projectName!,
                resolverOverride, setupFactory, localRepoOverride, ct)
                .ConfigureAwait(false);
        }

        // Offline mode: Codex tier discovery, write config, prompt operator.
        if (probeCodex is not null)
        {
            var probe = probeCodex();
            if (probe.Success && probe.Discovery is not null && probe.Discovery.Models.Count > 0)
            {
                var mapping = CodexTierMapper.Map(probe.Discovery);
                if (mapping is not null)
                {
                    var block = CodexSizesBlockRenderer.Render(mapping, probe.Discovery, commented: true);
                    content = CodexSizesBlockEditor.ReplaceCodexSizesBlock(content, block);
                }
            }
            else
            {
                console.ErrorWriteLine(
                    "Warning: could not discover Codex models (Codex may not be installed or not logged in); "
                    + "wrote the static Codex defaults. Run 'build models refresh' once Codex is available to update them.");
            }
        }

        var configDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(configDir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        console.WriteLine($"Created {target}");
        console.WriteLine("Fill in the REQUIRED fields before running other build commands.");
        console.WriteLine("Run 'build user-guide' to write the operator setup guide to docs/.");
        return 0;
    }

    /// <summary>
    /// Connected init: resolves or creates the Plane project, writes config with the resolved id,
    /// runs SetupCommand provisioning, verifies connectivity, and prints a summary.
    /// </summary>
    private static async Task<int> RunConnectedAsync(
        string cwd,
        string content,
        string target,
        IConsole console,
        Func<CodexProbeResult>? probeCodex,
        string planeUrl,
        string workspace,
        string effectiveToken,
        string projectName,
        IProjectResolver? resolverOverride,
        Func<string, (ITicketingProvisioner, ITicketingConnectivity)>? setupFactory,
        ILocalRepoOps? localRepoOverride,
        CancellationToken ct)
    {
        // Phase 1: resolve or create the Plane project.
        HttpClient? http = null;
        IProjectResolver resolver;
        if (resolverOverride is not null)
        {
            resolver = resolverOverride;
        }
        else
        {
            http = new HttpClient();
            resolver = new ProjectResolver(http, planeUrl, workspace, effectiveToken);
        }

        ProjectResolveResult resolveResult;
        try
        {
            resolveResult = await resolver.ResolveAsync(projectName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine($"Error: failed to resolve project '{projectName}': {ex.Message}");
            http?.Dispose();
            return 1;
        }

        // Phase 2: substitute resolved id into config content and write it.
        content = content.Replace("REQUIRED_PLANE_PROJECT_ID", resolveResult.ProjectId);

        if (probeCodex is not null)
        {
            var probe = probeCodex();
            if (probe.Success && probe.Discovery is not null && probe.Discovery.Models.Count > 0)
            {
                var mapping = CodexTierMapper.Map(probe.Discovery);
                if (mapping is not null)
                {
                    var block = CodexSizesBlockRenderer.Render(mapping, probe.Discovery, commented: true);
                    content = CodexSizesBlockEditor.ReplaceCodexSizesBlock(content, block);
                }
            }
            else
            {
                console.ErrorWriteLine(
                    "Warning: could not discover Codex models (Codex may not be installed or not logged in); "
                    + "wrote the static Codex defaults. Run 'build models refresh' once Codex is available to update them.");
            }
        }

        var configDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(configDir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        // Phase 3: setup provisioning (git init, .gitignore, states, labels).
        ITicketingProvisioner provisioner;
        ITicketingConnectivity connectivity;
        if (setupFactory is not null)
        {
            (provisioner, connectivity) = setupFactory(resolveResult.ProjectId);
        }
        else
        {
            http ??= new HttpClient();
            var client = new PlaneTicketingClient(http, new PlaneClientOptions
            {
                BaseUrl = planeUrl,
                WorkspaceSlug = workspace,
                ApiToken = effectiveToken,
                ProjectId = resolveResult.ProjectId,
            });
            provisioner = client;
            connectivity = client;
        }

        var localRepo = localRepoOverride ?? new FileSystemLocalRepoOps(cwd);
        var setupCmd = new SetupCommand(provisioner, localRepo);
        try
        {
            await setupCmd.ExecuteAsync(checkOnly: false, console, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine($"Error: setup provisioning failed: {ex.Message}");
            http?.Dispose();
            return 1;
        }

        // Phase 4: verify connectivity.
        TicketingConnectivityResult connectResult;
        try
        {
            connectResult = await connectivity.TestConnectivityAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine($"Error: connectivity check failed: {ex.Message}");
            http?.Dispose();
            return 1;
        }

        http?.Dispose();

        // Phase 5: print summary.
        var outcomeWord = resolveResult.Outcome == ProjectResolveOutcome.Created ? "created" : "found";
        console.WriteLine(string.Empty);
        console.WriteLine($"Project:      {projectName}");
        console.WriteLine($"Project id:   {resolveResult.ProjectId} ({outcomeWord})");
        console.WriteLine($"Connectivity: {(connectResult.Success ? "OK" : "FAILED")} - {connectResult.Message}");
        console.WriteLine($"Config:       {target}");

        if (!connectResult.Success)
        {
            console.ErrorWriteLine("Warning: connectivity check failed. Review the message above and re-run 'build setup --check' to diagnose.");
            return 1;
        }

        return 0;
    }

    private static void PromptForMissingValues(
        IConsole console,
        ref string? planeUrl,
        ref string? workspace,
        ref string? projectId,
        ref string? token)
    {
        if (planeUrl is null)
        {
            console.Write("Plane base URL: ");
            var response = console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(response)) planeUrl = response;
        }

        if (workspace is null)
        {
            console.Write("Plane workspace slug: ");
            var response = console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(response)) workspace = response;
        }

        if (projectId is null)
        {
            console.Write("Plane project ID: ");
            var response = console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(response)) projectId = response;
        }

        if (token is null)
        {
            console.Write("Plane API token (leave blank to fill in later): ");
            var response = console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(response)) token = response;
        }
    }

    /// <summary>
    /// Applies non-null fields from <paramref name="creds"/> to any parameter that is still null.
    /// Explicit flag values (already set before this call) are never overwritten.
    /// </summary>
    private static void ApplyCredsToParams(
        CredsFileValues creds,
        ref string? planeUrl,
        ref string? workspace,
        ref string? projectId,
        ref string? projectName,
        ref string? token)
    {
        planeUrl ??= creds.PlaneBaseUrl;
        workspace ??= creds.PlaneWorkspaceSlug;
        projectId ??= creds.PlaneProjectId;
        projectName ??= creds.PlaneProjectName;
        token ??= creds.PlaneApiToken;
    }

    /// <summary>
    /// Reads all remaining lines from <paramref name="console"/> until EOF (ReadLine returns null)
    /// and returns the joined result. Used to consume redirected stdin as a creds file.
    /// </summary>
    private static string ReadAllLines(IConsole console)
    {
        var sb = new System.Text.StringBuilder();
        string? line;
        while ((line = console.ReadLine()) is not null)
            sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>
    /// Applies flag values to the raw template string, replacing placeholders.
    /// </summary>
    public static string ApplyFlags(
        string template,
        string? planeUrl,
        string? workspace,
        string? projectId,
        string? token,
        string? tokenEnv)
    {
        var content = template;

        if (!string.IsNullOrEmpty(planeUrl))
            content = content.Replace("REQUIRED_PLANE_BASE_URL", planeUrl);

        if (!string.IsNullOrEmpty(workspace))
            content = content.Replace("REQUIRED_PLANE_WORKSPACE_SLUG", workspace);

        if (!string.IsNullOrEmpty(projectId))
            content = content.Replace("REQUIRED_PLANE_PROJECT_ID", projectId);

        // --token-env: replace the plane_api_token = "..." line with plane_api_token_env = "VALUE"
        // --token: replace just the placeholder value inside the existing line
        // The two flags are mutually exclusive: --token-env takes precedence if both are given.
        if (!string.IsNullOrEmpty(tokenEnv))
        {
            // Replace the literal token line with an env-var line.
            // Match the line that starts with plane_api_token = (not plane_api_token_env).
            content = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"plane_api_token\s*=\s*""[^""]*""([^\n]*)",
                $"plane_api_token_env = \"{tokenEnv}\"");
        }
        else if (!string.IsNullOrEmpty(token))
        {
            content = content.Replace("REQUIRED_PLANE_API_TOKEN", token);
        }

        return content;
    }
}
