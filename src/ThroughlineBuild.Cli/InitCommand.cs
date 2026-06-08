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
        bool noInteractive = false,
        IProjectDiscovery? discoveryOverride = null,
        Func<HttpClient>? httpClientFactory = null,
        CancellationToken ct = default)
    {
        var template = ConfigTemplateLoader.Load();
        // Each PlaneTicketingClient owns its own HttpClient: the client's constructor sets
        // BaseAddress and a default header, which throw once an HttpClient has sent a request,
        // so a single HttpClient can never back two clients. The factory is also the test seam
        // for driving the real connected/interactive path against a fake transport.
        var newHttpClient = httpClientFactory ?? (() => new HttpClient());

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

        // Interactive guided onboarding is available only at a real TTY, with no creds file and
        // no --no-interactive (so redirected stdin / automation never blocks on a prompt). When
        // eligible, prompt for the CONNECTION values (URL -> workspace -> token); the raw
        // "Plane project ID" GUID prompt is gone - the project id comes from create-or-pick.
        bool canPrompt = !console.IsInputRedirected && fromFile is null && !noInteractive;
        if (canPrompt)
            PromptForConnectionValues(console, ref planeUrl, ref workspace, ref token);

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

        // The effective token is the literal token flag value; if only --token-env is given,
        // we read that env var so the resolver can make actual API calls.
        var effectiveToken = token ?? (tokenEnv is not null ? Environment.GetEnvironmentVariable(tokenEnv) : null);

        // Interactive guided connect: a TTY operator who gave neither an explicit project id nor a
        // --project-name, but did supply (or enter) url + workspace + token, gets the create-or-pick
        // flow. A blank token means "I can't/won't connect" -> fall through to the offline template.
        bool canInteract = canPrompt
            && string.IsNullOrEmpty(projectId)
            && string.IsNullOrEmpty(projectName)
            && !string.IsNullOrEmpty(planeUrl)
            && !string.IsNullOrEmpty(workspace)
            && !string.IsNullOrEmpty(effectiveToken);

        if (canInteract)
        {
            return await RunInteractiveConnectedAsync(
                cwd, content, target, console, probeCodex,
                planeUrl!, workspace!, effectiveToken!,
                discoveryOverride, setupFactory, localRepoOverride, newHttpClient, ct)
                .ConfigureAwait(false);
        }

        // Non-interactive connected mode: --project-name provided, no explicit project id, and full
        // credentials available. An explicit projectId (from --project-id or the creds file's
        // plane_project_id key) takes precedence over resolution by name and stays offline.
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
                resolverOverride, setupFactory, localRepoOverride, newHttpClient, ct)
                .ConfigureAwait(false);
        }

        // Offline mode: Codex tier discovery, write config, print next steps.
        return WriteOfflineConfig(content, target, probeCodex, console);
    }

    /// <summary>
    /// Offline path: optionally enrich the Codex sizes block, write the config, and print the
    /// "what's still REQUIRED + next steps" guidance. Reused by the plain offline branch and by
    /// the interactive flow when the operator declines to connect.
    /// </summary>
    private static int WriteOfflineConfig(
        string content, string target, Func<CodexProbeResult>? probeCodex, IConsole console)
    {
        content = ApplyCodexProbe(content, probeCodex, console);

        var configDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(configDir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        console.WriteLine($"Created {target}");

        // Tailor the next-step guidance: name the specific config fields that are still
        // unresolved (so the operator does not have to diff the template), point at
        // 'build setup' as the required provisioning step, and surface connected mode as
        // the one-shot path that avoids pasting a project UUID.
        var stillRequired = new List<string>();
        if (content.Contains("REQUIRED_PLANE_BASE_URL")) stillRequired.Add("plane_base_url");
        if (content.Contains("REQUIRED_PLANE_WORKSPACE_SLUG")) stillRequired.Add("plane_workspace_slug");
        if (content.Contains("REQUIRED_PLANE_PROJECT_ID")) stillRequired.Add("plane_project_id");
        if (content.Contains("REQUIRED_PLANE_API_TOKEN")) stillRequired.Add("plane_api_token");

        if (stillRequired.Count > 0)
            console.WriteLine($"Still REQUIRED in {Path.GetFileName(target)}: {string.Join(", ", stillRequired)} - fill these in before running other build commands.");
        else
            console.WriteLine("Review the config values before running other build commands.");

        console.WriteLine("Next: run 'build setup' to provision the Plane project (states + labels) and initialize the local repo.");
        console.WriteLine("One-shot: re-run 'build init --project-name <name> --plane-url <url> --workspace <slug> --token <token>' (or interactive mode) to resolve or create the project and provision in one step - no UUID to paste.");
        console.WriteLine("Run 'build user-guide' to write the operator setup guide to docs/.");
        return 0;
    }

    /// <summary>
    /// If a Codex probe is injected, discover models and rewrite the [workers.codex.sizes]
    /// block; on probe failure leave the static defaults and print one actionable warning. Returns
    /// the (possibly updated) config content. Shared by the offline and connected write paths.
    /// </summary>
    private static string ApplyCodexProbe(string content, Func<CodexProbeResult>? probeCodex, IConsole console)
    {
        if (probeCodex is null)
            return content;

        var probe = probeCodex();
        if (probe.Success && probe.Discovery is not null && probe.Discovery.Models.Count > 0)
        {
            var mapping = CodexTierMapper.Map(probe.Discovery);
            if (mapping is not null)
            {
                var block = CodexSizesBlockRenderer.Render(mapping, probe.Discovery, commented: false);
                content = CodexSizesBlockEditor.ReplaceCodexSizesBlock(content, block);
            }
        }
        else
        {
            console.ErrorWriteLine(
                "Warning: could not discover Codex models (Codex may not be installed or not logged in); "
                + "wrote the static Codex defaults. Run 'build models refresh' once Codex is available to update them.");
        }
        return content;
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
        Func<HttpClient> newHttpClient,
        CancellationToken ct)
    {
        // Phase 1: resolve or create the Plane project by name (find-or-create). The resolver's
        // HttpClient is owned and disposed here; the pipeline creates its own (one client per
        // HttpClient - see ExecuteAsync).
        HttpClient? resolveHttp = null;
        ProjectResolveResult resolveResult;
        try
        {
            IProjectResolver resolver;
            if (resolverOverride is not null)
            {
                resolver = resolverOverride;
            }
            else
            {
                resolveHttp = newHttpClient();
                resolver = new ProjectResolver(resolveHttp, planeUrl, workspace, effectiveToken);
            }

            resolveResult = await resolver.ResolveAsync(projectName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine($"Error: failed to resolve project '{projectName}': {ex.Message}");
            return 1;
        }
        finally
        {
            resolveHttp?.Dispose();
        }

        return await RunConnectedPipelineAsync(
            cwd, content, target, console, probeCodex, planeUrl, workspace, effectiveToken,
            projectName, resolveResult, setupFactory, localRepoOverride, newHttpClient, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Interactive guided connect (the WI-07 flow): given an open connection, lets the operator
    /// create a new project or pick an existing one from a most-recently-used menu - no project
    /// UUID is ever typed or shown to copy - then runs the same connected pipeline. If the operator
    /// declines to connect, falls back to writing the offline template.
    /// </summary>
    private static async Task<int> RunInteractiveConnectedAsync(
        string cwd,
        string content,
        string target,
        IConsole console,
        Func<CodexProbeResult>? probeCodex,
        string planeUrl,
        string workspace,
        string effectiveToken,
        IProjectDiscovery? discoveryOverride,
        Func<string, (ITicketingProvisioner, ITicketingConnectivity)>? setupFactory,
        ILocalRepoOps? localRepoOverride,
        Func<HttpClient> newHttpClient,
        CancellationToken ct)
    {
        // The discovery client's HttpClient is owned and disposed here, before the pipeline builds
        // its own provisioning client on a fresh HttpClient (one client per HttpClient - see
        // ExecuteAsync). Sharing one HttpClient across both would throw on the second construction.
        HttpClient? discoveryHttp = null;
        (ProjectResolveResult Resolved, string DisplayName)? picked;
        try
        {
            IProjectDiscovery discovery;
            if (discoveryOverride is not null)
            {
                discovery = discoveryOverride;
            }
            else
            {
                discoveryHttp = newHttpClient();
                discovery = new PlaneTicketingClient(discoveryHttp, new PlaneClientOptions
                {
                    BaseUrl = planeUrl,
                    WorkspaceSlug = workspace,
                    ApiToken = effectiveToken,
                });
            }

            picked = await PromptCreateOrPickAsync(discovery, console, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.ErrorWriteLine($"Error: interactive project setup failed: {ex.Message}");
            return 1;
        }
        finally
        {
            discoveryHttp?.Dispose();
        }

        if (picked is null)
        {
            // Operator declined to connect at the create-or-pick prompt: write the offline template.
            return WriteOfflineConfig(content, target, probeCodex, console);
        }

        return await RunConnectedPipelineAsync(
            cwd, content, target, console, probeCodex, planeUrl, workspace, effectiveToken,
            picked.Value.DisplayName, picked.Value.Resolved, setupFactory, localRepoOverride, newHttpClient, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared phases 2-5 of connected init given an already-resolved project id: substitute the id
    /// into the config and write it, run setup provisioning, make the welcome commit, verify
    /// connectivity, and print the summary. Creates its own provisioning HttpClient from
    /// <paramref name="newHttpClient"/> (one client per HttpClient) and disposes it. Reused by both
    /// name-based connected init and the interactive create-or-pick flow.
    /// </summary>
    private static async Task<int> RunConnectedPipelineAsync(
        string cwd,
        string content,
        string target,
        IConsole console,
        Func<CodexProbeResult>? probeCodex,
        string planeUrl,
        string workspace,
        string effectiveToken,
        string projectName,
        ProjectResolveResult resolveResult,
        Func<string, (ITicketingProvisioner, ITicketingConnectivity)>? setupFactory,
        ILocalRepoOps? localRepoOverride,
        Func<HttpClient> newHttpClient,
        CancellationToken ct)
    {
        // Phase 2: substitute resolved id into config content (after the id is known) and write it.
        content = content.Replace("REQUIRED_PLANE_PROJECT_ID", resolveResult.ProjectId);
        content = ApplyCodexProbe(content, probeCodex, console);

        var configDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(configDir);
        File.WriteAllText(target, content, System.Text.Encoding.UTF8);

        // Phase 3: setup provisioning (git init, .gitignore, states, labels). The provisioning
        // client gets its own fresh HttpClient (never the discovery/resolver one - that already
        // sent requests, so reusing it would throw when this client sets BaseAddress).
        ITicketingProvisioner provisioner;
        ITicketingConnectivity connectivity;
        HttpClient? http = null;
        if (setupFactory is not null)
        {
            (provisioner, connectivity) = setupFactory(resolveResult.ProjectId);
        }
        else
        {
            http = newHttpClient();
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

        // Phase 3.5: welcome commit (fresh repo only). SetupCommand (phase 3) already runs this
        // for the fresh-repo path; the shared helper's HasAnyCommits guard makes this redundant
        // call a no-op so connected init still produces exactly one welcome commit.
        WelcomeCommit.EnsureInitialCommit(localRepo, console);

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
        var docPaths = FindDocPaths(cwd);
        var outcomeWord = resolveResult.Outcome == ProjectResolveOutcome.Created ? "created" : "found";
        console.WriteLine(string.Empty);
        console.WriteLine($"Project:      {projectName}");
        console.WriteLine($"Project id:   {resolveResult.ProjectId} ({outcomeWord})");
        console.WriteLine($"Connectivity: {(connectResult.Success ? "OK" : "FAILED")} - {connectResult.Message}");
        console.WriteLine($"Config:       {target}");
        foreach (var docPath in docPaths)
        {
            console.WriteLine($"Op doc:       {docPath}");
            console.WriteLine($"Scaffold:     run 'build scaffold {docPath}'");
        }

        if (!connectResult.Success)
        {
            console.ErrorWriteLine("Warning: connectivity check failed. Review the message above and re-run 'build setup --check' to diagnose.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Asks "create a new project or use an existing one?" and returns the resolved id + a display
    /// name, or null when the operator declines (blank) so the caller can fall back to offline.
    /// Choosing "existing" with no projects in the workspace (or pressing 'n' at the menu) falls
    /// through to the create path.
    /// </summary>
    private static async Task<(ProjectResolveResult Resolved, string DisplayName)?> PromptCreateOrPickAsync(
        IProjectDiscovery discovery, IConsole console, CancellationToken ct)
    {
        while (true)
        {
            console.Write("Create a new project or use an existing one? [c/e] (blank to skip and write offline template): ");
            var choice = console.ReadLine()?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(choice))
                return null; // decline -> offline template

            if (choice is "e" or "existing")
            {
                var existing = await PickExistingProjectAsync(discovery, console, ct).ConfigureAwait(false);
                if (existing is not null)
                    return (new ProjectResolveResult(existing.Value.Id, ProjectResolveOutcome.Found), existing.Value.Name);
                // No projects, or operator chose 'n' at the menu -> create instead.
                return await CreateNewProjectAsync(discovery, console, ct).ConfigureAwait(false);
            }

            if (choice is "c" or "n" or "new" or "create")
                return await CreateNewProjectAsync(discovery, console, ct).ConfigureAwait(false);

            console.WriteLine("Please enter 'c' (create) or 'e' (existing).");
        }
    }

    /// <summary>
    /// Lists workspace projects most-recently-used first and lets the operator pick one by number.
    /// Returns the chosen project's id + name, or null when there are no projects or the operator
    /// pressed 'n' to create a new one instead. The project UUID is never shown as something to copy.
    /// </summary>
    private static async Task<(string Id, string Name)?> PickExistingProjectAsync(
        IProjectDiscovery discovery, IConsole console, CancellationToken ct)
    {
        var projects = (await discovery.ListProjectsAsync(ct).ConfigureAwait(false))
            .OrderByDescending(p => p.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        if (projects.Count == 0)
        {
            console.WriteLine("No existing projects in this workspace - let's create one.");
            return null;
        }

        console.WriteLine("Select a project:");
        for (int i = 0; i < projects.Count; i++)
        {
            var p = projects[i];
            var updated = p.UpdatedAt is { } u ? u.ToString("yyyy-MM-dd") : "unknown";
            console.WriteLine($"  {i + 1}) {p.Name}  ({p.Identifier})  updated {updated}");
        }

        while (true)
        {
            console.Write("Enter number (or 'n' to create new): ");
            var input = console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                console.WriteLine("Please enter a number or 'n'.");
                continue;
            }
            if (input.Equals("n", StringComparison.OrdinalIgnoreCase) || input.Equals("new", StringComparison.OrdinalIgnoreCase))
                return null; // -> create new
            if (int.TryParse(input, out var n) && n >= 1 && n <= projects.Count)
            {
                var chosen = projects[n - 1];
                return (chosen.Id, chosen.Name);
            }
            console.WriteLine($"Please enter a number between 1 and {projects.Count}, or 'n'.");
        }
    }

    /// <summary>
    /// Prompts for a project name and a Plane identifier (offering a derived default the operator can
    /// accept or override), validates the identifier, creates the project, and returns its new id.
    /// </summary>
    private static async Task<(ProjectResolveResult Resolved, string DisplayName)?> CreateNewProjectAsync(
        IProjectDiscovery discovery, IConsole console, CancellationToken ct)
    {
        string name;
        while (true)
        {
            console.Write("Project name: ");
            var n = console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(n)) { name = n; break; }
            console.WriteLine("A project name is required.");
        }

        var defaultId = ProjectResolver.DeriveIdentifier(name);
        string identifier;
        while (true)
        {
            console.Write($"Project identifier [{defaultId}]: ");
            var raw = console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(raw)) { identifier = defaultId; break; }
            var normalized = raw.ToUpperInvariant();
            if (IsValidIdentifier(normalized)) { identifier = normalized; break; }
            console.WriteLine("Identifier must be 2-10 letters/digits starting with a letter (e.g. ST).");
        }

        var newId = await discovery.CreateProjectAsync(name, identifier, ct).ConfigureAwait(false);
        return (new ProjectResolveResult(newId, ProjectResolveOutcome.Created), name);
    }

    /// <summary>Plane identifier rules used to validate operator input: 2-10 ASCII letters/digits, starting with a letter.</summary>
    private static bool IsValidIdentifier(string id)
    {
        if (id.Length is < 2 or > 10) return false;
        if (!char.IsAsciiLetter(id[0])) return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// Scans the canonical op-doc directories for markdown files: the conventional
    /// docs/op-docs and docs/proposals, plus their top-level op-docs and proposals
    /// equivalents (where operators commonly drop a doc). Returns paths relative to
    /// <paramref name="cwd"/> using forward slashes, suitable for use as arguments to
    /// 'build scaffold'. Detection stays bounded to this fixed allow-list; arbitrary repo
    /// markdown is never scanned. Each file is reported at most once even if two scanned
    /// directories resolve to the same path.
    /// </summary>
    public static IReadOnlyList<string> FindDocPaths(string cwd)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] relDirs = ["docs/op-docs", "docs/proposals", "op-docs", "proposals"];
        foreach (var relDir in relDirs)
        {
            var absDir = Path.Combine(cwd, relDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absDir))
                continue;
            foreach (var file in Directory.GetFiles(absDir, "*.md").OrderBy(f => f))
            {
                var rel = Path.GetRelativePath(cwd, file).Replace(Path.DirectorySeparatorChar, '/');
                if (seen.Add(rel))
                    results.Add(rel);
            }
        }
        return results;
    }

    // Prompts for the CONNECTION values only - base URL, workspace slug, API token - in the order
    // the operator expects. The raw "Plane project ID" GUID prompt is intentionally gone (WI-07):
    // the project id is obtained via the interactive create-or-pick flow, never by pasting a UUID.
    // A blank token is left null so the caller falls back to writing the offline template.
    private static void PromptForConnectionValues(
        IConsole console,
        ref string? planeUrl,
        ref string? workspace,
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
