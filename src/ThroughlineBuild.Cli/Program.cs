using System.Net.Http;
using System.Reflection;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using ThroughlineBuild.ModelClient;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;
using ThroughlineBuild.Workers.ClaudeCode;
using ThroughlineBuild.Workers.Codex;
using ThroughlineBuild.Workers.Copilot;
using ThroughlineBuild.Workers.Gemini;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
    {
        Console.WriteLine(CliUsage.UsageText);
        return 0;
    }

    // Pre-pass: strip --debug, --quiet, --summary-json, and --error-location from args before any positional parser sees them.
    // All four are bare bool flags (no value); the existing key/value parser expects pairs
    // and would mangle subsequent args if they were left in.
    bool debugMode = false;
    bool quietMode = false;
    bool summaryJson = false;
    bool errorLocation = false;
    bool noAutoResolve = false;
    bool noAutoMerge = false;
    bool continuePastFailure = false;
    var filteredArgs = new List<string>(args.Length);
    foreach (var a in args)
    {
        if (a == "--debug")
            debugMode = true;
        else if (a == "--quiet")
            quietMode = true;
        else if (a == "--summary-json")
            summaryJson = true;
        else if (a == "--error-location")
            errorLocation = true;
        else if (a == "--no-auto-resolve")
            noAutoResolve = true;
        else if (a == "--no-auto-merge")
            noAutoMerge = true;
        else if (a == "--continue-past-failure")
            continuePastFailure = true;
        else
            filteredArgs.Add(a);
    }
    args = filteredArgs.ToArray();

    // Second pre-pass: extract --agent / --agent-<phase> flag pairs before verb dispatch.
    var (agentAll, agentPlanFlag, agentImplementFlag, agentReviewFlag, argsAfterAgentFlags) =
        CliArgParser.ExtractAgentFlags(args);
    args = ((List<string>)argsAfterAgentFlags).ToArray();

    var verb = args[0];

    // Extract ticket IDs for verbs that need them.
    IReadOnlyList<string> ticketIds = Array.Empty<string>();
    if (verb == "plan" || verb == "implement" || verb == "review" || verb == "ship" || verb == "chain" || verb == "rework" || verb == "decompose")
    {
        var (extracted, _) = CliArgParser.ExtractTicketIds(args);
        ticketIds = extracted;
    }

    // Arg validation for phase verbs happens BEFORE config load so a missing id
    // surfaces a usage error (exit 2) rather than a config-not-found error.
    if (verb == "plan" || verb == "implement" || verb == "review" || verb == "ship" || verb == "chain" || verb == "rework" || verb == "decompose")
    {
        if (ticketIds.Count == 0 || string.IsNullOrWhiteSpace(ticketIds[0]))
        {
            Console.Error.WriteLine("Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id> [ticket-id ...]");
            return 2;
        }

        // For rework and decompose: reject multiple positional ticket IDs (single ticket only).
        if ((verb == "rework" || verb == "decompose") && ticketIds.Count > 1)
        {
            Console.Error.WriteLine($"Error: build {verb} accepts exactly one ticket ID; multi-ticket dispatch is not supported.");
            return 2;
        }
    }

    // Early arg validation for scaffold verb: op-doc-path is a required positional.
    if (verb == "scaffold")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
        {
            Console.Error.WriteLine("Error: op-doc-path is required");
            Console.Error.WriteLine("Usage: build scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--debug]");
            return 2;
        }
    }

    // Early validation for new verb using the argument classifier.
    if (verb == "new")
    {
        var earlyClassification = NewVerbArgumentClassifier.Classify(args);
        if (earlyClassification.Kind == NewVerbKind.HelpExitNonZero)
        {
            Console.Error.WriteLine("Error: body-path is required");
            Console.Error.WriteLine("Usage: build new <body-path> [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--debug]");
            Console.Error.WriteLine("       build new <text> [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--debug]");
            Console.Error.WriteLine("       build new - [--title \"...\"] [--type \"...\"] [--label \"...\"]* [--debug]");
            Console.Error.WriteLine("       build new --print-template");
            return 2;
        }
    }

    // Resolve the main worktree root via git worktree list so that phases receive a
    // sane workingDirectory even when the CLI is invoked from inside a feature worktree.
    // Fall back to the raw cwd on any error so no new failure mode is introduced.
    var rawCwd = Directory.GetCurrentDirectory();

    // 'build init' must run before config load - it bootstraps the config file.
    if (verb == "init")
    {
        var force = filteredArgs.Contains("--force");
        var printTemplate = filteredArgs.Contains("--print-template");
        var initPlaneUrl  = CliArgParser.GetFlagValue(filteredArgs, "--plane-url");
        var initWorkspace = CliArgParser.GetFlagValue(filteredArgs, "--workspace");
        var initProjectId = CliArgParser.GetFlagValue(filteredArgs, "--project-id");
        var initToken     = CliArgParser.GetFlagValue(filteredArgs, "--token");
        var initTokenEnv  = CliArgParser.GetFlagValue(filteredArgs, "--token-env");
        return InitCommand.Execute(rawCwd, force, printTemplate, SystemConsole.Instance,
            planeUrl: initPlaneUrl,
            workspace: initWorkspace,
            projectId: initProjectId,
            token: initToken,
            tokenEnv: initTokenEnv);
    }

    var resolverGit = new ProcessGitClient(rawCwd);
    var resolvedCwd = await MainWorktreeResolver.ResolveAsync(resolverGit, rawCwd, CancellationToken.None);

    var cwd2 = resolvedCwd;

    string configPath2;
    try
    {
        configPath2 = BuildConfigLoader.FindConfigFile(cwd2)
            ?? throw new ConfigException($"config file not found: searched from {cwd2} upwards for .build/config.toml");
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
        return 2;
    }

    BuildConfig config2;
    try
    {
        config2 = BuildConfigLoader.Load(configPath2);
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
        return 2;
    }

    BuildSecrets secrets2;
    try
    {
        secrets2 = BuildConfigLoader.ResolveSecrets(config2);
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Secret error: {ex.Message}");
        if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
        return 3;
    }

    string ResolveLogDir(string raw) => Path.GetFullPath(BuildConfigLoader.ResolveLogDirectory(configPath2, raw, cwd2));

    var buildVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    var sessionContext = new SessionContext(
        ProjectId: config2.Ticketing.PlaneProjectId,
        ProjectName: string.IsNullOrEmpty(config2.Ticketing.PlaneProjectName) ? null : config2.Ticketing.PlaneProjectName,
        WorkspaceSlug: config2.Ticketing.PlaneWorkspaceSlug,
        BuildVersion: buildVersion);

    if (verb == "list")
    {
        var http2 = new HttpClient();
        var ticketing2 = new PlaneTicketingClient(http2, new PlaneClientOptions
        {
            BaseUrl = config2.Ticketing.PlaneBaseUrl,
            ApiToken = secrets2.PlaneApiToken,
            WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
            ProjectId = config2.Ticketing.PlaneProjectId,
            ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
        });

        var cmd = new ListCommand(ticketing2, Console.Out);
        var extraArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            var key = args[i];
            if (key.StartsWith("--"))
                key = key.Substring(2);
            extraArgs[key] = args[i + 1];
        }
        var ctx = new TicketCommandContext("", extraArgs);

        try
        {
            using var verbCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
            var verbResult = await cmd.ExecuteAsync(ctx, verbCts.Token);
            if (!verbResult.Success)
            {
                Console.Error.WriteLine($"Command 'list' failed: {verbResult.Message}");
                return 1;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }

    if (verb == "amend" || verb == "close" || verb == "defer" || verb == "reopen")
    {
        var sessionId2 = Guid.NewGuid().ToString("N");
        var verbTicketIdForName = args.Length >= 2 ? args[1] : null;
        var fileStem2 = SessionFileNameBuilder.Build(
            projectName: config2.Ticketing.PlaneProjectName,
            projectIdentifier: config2.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: verbTicketIdForName,
            extraSlug: null,
            timestamp: DateTimeOffset.Now);
        var http2 = new HttpClient();
        var ticketing2 = new PlaneTicketingClient(http2, new PlaneClientOptions
        {
            BaseUrl = config2.Ticketing.PlaneBaseUrl,
            ApiToken = secrets2.PlaneApiToken,
            WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
            ProjectId = config2.Ticketing.PlaneProjectId,
            ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
        });
        await using var jsonlEventSink2 = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = sessionId2,
            FileNameStem = fileStem2
        }, sessionContext);
        var eventSink2 = new RecordingEventSink(jsonlEventSink2);

        var registry = new TicketCommandRegistry();
        registry.Register("amend", new AmendCommand(ticketing2, eventSink2));

        var wireUpError = WireUpConditionalCommands(verb, registry, secrets2, config2.Llm, http2, ticketing2, eventSink2, cwd2);
        if (wireUpError is not null)
        {
            Console.Error.WriteLine($"Secret error: {wireUpError}");
            return 3;
        }
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
            return 2;
        }

        var verbTicketId = args[1];
        var extraArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        int parseStart = 2;
        if ((verb == "close" || verb == "defer" || verb == "reopen") && args.Length >= 3 && !args[2].StartsWith("--"))
        {
            extraArgs["reason"] = args[2];
            parseStart = 3;
        }
        // Single-pass: bare bool flags consume 1 slot; key=value pairs consume 2.
        for (int i = parseStart; i < args.Length; )
        {
            if (args[i] == "--no-cascade")
            {
                extraArgs["no-cascade"] = "true";
                i += 1;
            }
            else if (i + 1 < args.Length)
            {
                var key = args[i];
                if (key.StartsWith("--"))
                    key = key.Substring(2);
                extraArgs[key] = args[i + 1];
                i += 2;
            }
            else
            {
                // Lone flag with no value - skip to avoid out-of-bounds.
                i += 1;
            }
        }
        var ctx = new TicketCommandContext(verbTicketId, extraArgs);

        if (!registry.TryGet(verb, out var cmd) || cmd is null)
        {
            Console.Error.WriteLine($"Verb '{verb}' is not yet implemented.");
            return 1;
        }

        try
        {
            using var verbCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
            var verbResult = await cmd.ExecuteAsync(ctx, verbCts.Token);
            if (!verbResult.Success)
            {
                Console.Error.WriteLine($"Command '{verb}' failed: {verbResult.Message}");
                return 1;
            }
            if (!string.IsNullOrEmpty(verbResult.Message))
                Console.WriteLine(verbResult.Message);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }

    if (verb == "new")
    {
        var sessionId2 = Guid.NewGuid().ToString("N");
        var fileStem2 = SessionFileNameBuilder.Build(
            projectName: config2.Ticketing.PlaneProjectName,
            projectIdentifier: config2.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: null,
            extraSlug: null,
            timestamp: DateTimeOffset.Now);
        var http2 = new HttpClient();
        var ticketing2 = new PlaneTicketingClient(http2, new PlaneClientOptions
        {
            BaseUrl = config2.Ticketing.PlaneBaseUrl,
            ApiToken = secrets2.PlaneApiToken,
            WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
            ProjectId = config2.Ticketing.PlaneProjectId,
            ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
        });
        await using var jsonlEventSink2 = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = sessionId2,
            FileNameStem = fileStem2
        }, sessionContext);
        var eventSink2 = new RecordingEventSink(jsonlEventSink2);

        // Classify argument shape to determine file-mode vs draft-mode.
        var classification = NewVerbArgumentClassifier.Classify(args);

        // Build options: file-mode only needs a stub (no worker); draft-mode needs a real worker.
        bool needsWorker = classification.Kind == NewVerbKind.DraftMode
                        || classification.Kind == NewVerbKind.StdinDraftMode;

        string? debugCaptureDir2 = debugMode
            ? Path.GetFullPath(Path.Combine(cwd2, ".build", "sessions", fileStem2))
            : null;
        if (debugCaptureDir2 is not null)
            Directory.CreateDirectory(debugCaptureDir2);

        BuildOptions buildOptions2;
        if (needsWorker)
        {
            buildOptions2 = new BuildOptions(
                SessionId: sessionId2,
                WorkerName: config2.Workers.DefaultAgent,
                WorkerTimeout: TimeSpan.FromMinutes(config2.Workers.TimeoutMinutes),
                DebugCaptureDirectory: debugCaptureDir2,
                LiveStdoutSink: debugMode ? Console.Out : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: (!debugMode && !quietMode
                    && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1"))
                    ? Console.Error : null);
        }
        else
        {
            buildOptions2 = new BuildOptions(
                SessionId: sessionId2,
                WorkerName: "",
                WorkerTimeout: TimeSpan.Zero,
                DebugCaptureDirectory: debugCaptureDir2);
        }

        var newPhase2 = new NewPhase(ticketing2, eventSink2, buildOptions2);

        var newCommandArgs = new Dictionary<string, string>(StringComparer.Ordinal);

        // Parse flags: --title, --type, --label (repeatable), --review; --debug already stripped.
        var labels = new List<string>();
        bool review = false;
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--print-template")
            {
                // handled by classifier
            }
            else if (arg == "--review")
            {
                review = true;
            }
            else if (arg == "--title" && i + 1 < args.Length)
            {
                newCommandArgs["title"] = args[++i];
            }
            else if (arg == "--type" && i + 1 < args.Length)
            {
                newCommandArgs["type"] = args[++i];
            }
            else if (arg == "--label" && i + 1 < args.Length)
            {
                labels.Add(args[++i]);
            }
        }
        if (labels.Count > 0)
            newCommandArgs["labels"] = string.Join("\t", labels);

        using var verbCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };

        // --- PrintTemplate path ---
        if (classification.Kind == NewVerbKind.PrintTemplate)
        {
            newCommandArgs["print_template"] = "true";
            var ctx2 = new TicketCommandContext("", newCommandArgs);
            var cmd2 = new NewCommand(newPhase2, config2.Ticketing.PlaneBaseUrl, config2.Ticketing.PlaneWorkspaceSlug, buildOptions2.DebugCaptureDirectory);
            try
            {
                var verbResult = await cmd2.ExecuteAsync(ctx2, verbCts.Token);
                if (!verbResult.Success)
                {
                    Console.Error.WriteLine($"Command 'new' failed: {verbResult.Message}");
                    return 1;
                }
                if (!string.IsNullOrEmpty(verbResult.Message))
                    Console.WriteLine(verbResult.Message);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // --- File mode path ---
        if (classification.Kind == NewVerbKind.FileMode)
        {
            newCommandArgs["body_path"] = classification.FilePath!;
            var ctx2 = new TicketCommandContext("", newCommandArgs);
            var cmd2 = new NewCommand(newPhase2, config2.Ticketing.PlaneBaseUrl, config2.Ticketing.PlaneWorkspaceSlug, buildOptions2.DebugCaptureDirectory);
            try
            {
                var verbResult = await cmd2.ExecuteAsync(ctx2, verbCts.Token);
                if (!verbResult.Success)
                {
                    Console.Error.WriteLine($"Command 'new' failed: {verbResult.Message}");
                    if (buildOptions2.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                    return 1;
                }
                if (!string.IsNullOrEmpty(verbResult.Message))
                    Console.WriteLine(verbResult.Message);
                if (buildOptions2.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                if (buildOptions2.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                return 1;
            }
        }

        // --- Draft mode (DraftMode or StdinDraftMode) ---
        string draftText;
        if (classification.Kind == NewVerbKind.StdinDraftMode)
        {
            draftText = Console.In.ReadToEnd();
        }
        else
        {
            // DraftMode: may need to emit a warning notice and brief pause.
            draftText = classification.DraftText!;
            if (classification.LooksLikePathButMissing)
            {
                Console.Error.WriteLine(
                    $"note: no file at '{draftText}'; treating as draft input. Press Ctrl-C to abort.");
                Thread.Sleep(500);
            }
        }

        // Build a real worker for draft mode using the implement phase agent (or default).
        var draftImplementAgentName = config2.Workers.Phases.TryGetValue("implement", out var draftPhaseName)
            ? draftPhaseName
            : config2.Workers.DefaultAgent;
        if (!config2.Workers.Agents.TryGetValue(draftImplementAgentName, out var draftAgentCfg))
            throw new ConfigException($"missing [workers.{draftImplementAgentName}] sub-table in config");
        var draftWorkerFactory = new WorkerAgentFactory(
            new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
            {
                [draftImplementAgentName] = () => new ClaudeCodeAgent(new ClaudeCodeOptions
                {
                    ExecutablePath = draftAgentCfg.Executable,
                    MaxOutputTokens = draftAgentCfg.MaxOutputTokens,
                    Sizes = draftAgentCfg.Sizes
                })
            });
        var draftWorker = draftWorkerFactory.Create(draftImplementAgentName);

        var draftPhase = new DraftPhase(draftWorker, buildOptions2);
        var draftSw = System.Diagnostics.Stopwatch.StartNew();
        DraftResult draftResult;
        try
        {
            draftResult = await draftPhase.RunAsync(
                new DraftPhaseOptions(draftText, debugMode),
                cwd2,
                verbCts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            if (buildOptions2.DebugCaptureDirectory is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
            return 1;
        }
        draftSw.Stop();

        if (draftResult.Outcome != ThroughlineBuild.Contracts.Models.DraftOutcome.Ok)
        {
            Console.Error.WriteLine($"draft failed: {draftResult.FailureReason}");
            if (buildOptions2.DebugCaptureDirectory is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
            return 1;
        }

        Console.WriteLine($"[drafted] from operator text ({draftSw.Elapsed.TotalSeconds:0.0}s)");

        // When --review is set, run interactive review loop before filing.
        string finalBody;
        if (review)
        {
            var loop = new ReviewLoop(
                SystemConsole.Instance,
                (text, ct2) => draftPhase.RunAsync(new DraftPhaseOptions(text, debugMode), cwd2, ct2),
                ReviewLoop.DefaultEditorResolver());
            var loopResult = await loop.RunAsync(draftResult.BodyMarkdown!, draftText, verbCts.Token);
            if (loopResult.Outcome == ReviewLoopOutcome.Aborted)
            {
                Console.WriteLine("no ticket filed");
                return 0;
            }
            finalBody = loopResult.FinalBody!;
        }
        else
        {
            finalBody = draftResult.BodyMarkdown!;
        }

        // Write body markdown to a temp file; NewCommand expects a file path.
        var tempBodyPath = Path.ChangeExtension(Path.GetTempFileName(), ".md");
        try
        {
            File.WriteAllText(tempBodyPath, finalBody);
            newCommandArgs["body_path"] = tempBodyPath;

            var ctx3 = new TicketCommandContext("", newCommandArgs);
            var cmd3 = new NewCommand(newPhase2, config2.Ticketing.PlaneBaseUrl, config2.Ticketing.PlaneWorkspaceSlug, buildOptions2.DebugCaptureDirectory);
            try
            {
                var verbResult = await cmd3.ExecuteAsync(ctx3, verbCts.Token);
                if (!verbResult.Success)
                {
                    Console.Error.WriteLine($"Command 'new' failed: {verbResult.Message}");
                    if (buildOptions2.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                    return 1;
                }
                if (!string.IsNullOrEmpty(verbResult.Message))
                    Console.WriteLine(verbResult.Message);
                if (buildOptions2.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                if (buildOptions2.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{fileStem2}/");
                return 1;
            }
        }
        finally
        {
            try { File.Delete(tempBodyPath); } catch { /* best effort cleanup */ }
        }
    }

    if (verb == "scaffold")
    {
        var scaffoldSessionId = Guid.NewGuid().ToString("N");
        var opDocStem = Path.GetFileNameWithoutExtension(args[1]);
        var scaffoldFileStem = SessionFileNameBuilder.Build(
            projectName: config2.Ticketing.PlaneProjectName,
            projectIdentifier: config2.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: null,
            extraSlug: opDocStem,
            timestamp: DateTimeOffset.Now);
        var scaffoldHttp = new HttpClient();
        var scaffoldTicketing = new PlaneTicketingClient(scaffoldHttp, new PlaneClientOptions
        {
            BaseUrl = config2.Ticketing.PlaneBaseUrl,
            ApiToken = secrets2.PlaneApiToken,
            WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
            ProjectId = config2.Ticketing.PlaneProjectId,
            ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
        });
        await using var scaffoldJsonlSink = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = scaffoldSessionId,
            FileNameStem = scaffoldFileStem
        }, sessionContext);
        var scaffoldEventSink = new RecordingEventSink(scaffoldJsonlSink);

        var scaffoldPhase = new ScaffoldPhase(scaffoldTicketing, scaffoldEventSink, scaffoldSessionId);
        var scaffoldCommand = new ScaffoldCommand(scaffoldPhase);

        // Parse scaffold-local flags.
        var scaffoldArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        scaffoldArgs["op_doc_path"] = args[1];
        for (int i = 2; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--validate-only") scaffoldArgs["validate_only"] = "true";
            else if (a == "--dry-run") scaffoldArgs["dry_run"] = "true";
            else if (a == "--accept-warnings") scaffoldArgs["accept_warnings"] = "true";
            // --debug and --error-location already stripped by pre-pass; other unknown flags are silently ignored
        }
        if (errorLocation) scaffoldArgs["show_location"] = "true";

        var scaffoldCtx = new TicketCommandContext("", scaffoldArgs);

        try
        {
            using var scaffoldCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; scaffoldCts.Cancel(); };
            var scaffoldResult = await scaffoldCommand.ExecuteAsync(scaffoldCtx, scaffoldCts.Token);

            // Strip the EXIT: tag from the message to get the human-readable output.
            var msg = scaffoldResult.Message ?? string.Empty;
            int firstNl = msg.IndexOf('\n');
            string tag = firstNl >= 0 ? msg.Substring(0, firstNl) : msg;
            string body = firstNl >= 0 ? msg.Substring(firstNl + 1) : string.Empty;

            if (!string.IsNullOrEmpty(body))
            {
                if (!scaffoldResult.Success)
                    Console.Error.WriteLine(body);
                else
                    Console.WriteLine(body);
            }

            return tag switch
            {
                ScaffoldExitCategory.Clean => 0,
                ScaffoldExitCategory.ValidationError => 2,
                ScaffoldExitCategory.PartialCreation => 3,
                _ => scaffoldResult.Success ? 0 : 1
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }

    if (verb != "plan" && verb != "implement" && verb != "review" && verb != "ship" && verb != "chain" && verb != "rework" && verb != "decompose")
    {
        Console.Error.WriteLine($"Unknown subcommand: {verb}");
        Console.Error.WriteLine(CliUsage.UsageText);
        return 2;
    }

    var cwd = resolvedCwd;

    var http = new HttpClient();
    var ticketing = new PlaneTicketingClient(http, new PlaneClientOptions
    {
        BaseUrl = config2.Ticketing.PlaneBaseUrl,
        ApiToken = secrets2.PlaneApiToken,
        WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
        ProjectId = config2.Ticketing.PlaneProjectId,
        ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
    });
    if (!config2.Workers.Agents.TryGetValue(config2.Workers.DefaultAgent, out var agentCfg))
        throw new ConfigException($"missing [workers.{config2.Workers.DefaultAgent}] sub-table in config");

    // Collect all agent names referenced by phases (plus default) and register them.
    // Also include any names supplied via CLI flags so they are available to the factory
    // (and so that unknown names surface as a clear ConfigException from factory.Create).
    var allAgentNames = new HashSet<string>(StringComparer.Ordinal) { config2.Workers.DefaultAgent };
    foreach (var phaseName in config2.Workers.Phases.Values)
        allAgentNames.Add(phaseName);
    foreach (var flagAgentName in new[] { agentAll, agentPlanFlag, agentImplementFlag, agentReviewFlag })
    {
        if (flagAgentName is not null && config2.Workers.Agents.ContainsKey(flagAgentName))
            allAgentNames.Add(flagAgentName);
    }

    var factoryEntries = new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal);
    foreach (var agentName in allAgentNames)
    {
        if (!config2.Workers.Agents.TryGetValue(agentName, out var aCfg))
            throw new ConfigException($"missing [workers.{agentName}] sub-table in config");
        var capturedCfg = aCfg;
        var capturedName = agentName;
        factoryEntries[agentName] = () =>
        {
            if (capturedName == "gemini")
                return new GeminiAgent(new GeminiOptions
                {
                    ExecutablePath = capturedCfg.Executable,
                    MaxOutputTokens = capturedCfg.MaxOutputTokens,
                    Sizes = capturedCfg.Sizes,
                    BypassPermissions = capturedCfg.BypassPermissions
                });
            if (capturedName == "codex")
                return new CodexAgent(new CodexOptions
                {
                    ExecutablePath = capturedCfg.Executable,
                    MaxOutputTokens = capturedCfg.MaxOutputTokens,
                    Sizes = capturedCfg.Sizes,
                    BypassPermissions = capturedCfg.BypassPermissions
                });
            if (capturedName == "copilot")
                return new CopilotAgent(new CopilotOptions
                {
                    ExecutablePath = capturedCfg.Executable,
                    MaxOutputTokens = capturedCfg.MaxOutputTokens,
                    Sizes = capturedCfg.Sizes
                });
            return new ClaudeCodeAgent(new ClaudeCodeOptions
            {
                ExecutablePath = capturedCfg.Executable,
                MaxOutputTokens = capturedCfg.MaxOutputTokens,
                Sizes = capturedCfg.Sizes,
                BypassPermissions = capturedCfg.BypassPermissions
            });
        };
    }
    var workerFactory = new WorkerAgentFactory(factoryEntries);

    // Helper: resolve the agent name for a given phase, falling back to default_agent.
    string AgentFor(string phase) =>
        config2.Workers.Phases.TryGetValue(phase, out var n) ? n : config2.Workers.DefaultAgent;

    // Helper: apply CLI flag overrides on top of config. Per-phase flag wins over --agent,
    // which wins over config. Phases not listed here fall back to AgentFor(phase).
    string EffectiveAgentFor(string phase) =>
        phase == "plan"      ? (agentPlanFlag ?? agentAll ?? AgentFor("plan")) :
        phase == "implement" ? (agentImplementFlag ?? agentAll ?? AgentFor("implement")) :
        phase == "review"    ? (agentReviewFlag ?? agentAll ?? AgentFor("review")) :
        AgentFor(phase);

    // Shared git client (read-only ops only at this layer) for summary-block construction.
    var summaryGit = new ProcessGitClient(cwd);

    void WriteSummary(PhaseSummary summary)
    {
        var text = summaryJson
            ? PhaseSummaryRenderer.RenderJson(summary)
            : PhaseSummaryRenderer.RenderText(summary);
        Console.Out.Write(text);
        if (!text.EndsWith('\n')) Console.Out.WriteLine();
    }

    // Loop over each ticket ID for multi-ticket dispatch.
    // Sequential: stop on first failure (return non-zero exit code).
    int dispatchExitCode = 0;
    foreach (var ticketId in ticketIds)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var fileStem = SessionFileNameBuilder.Build(
            projectName: config2.Ticketing.PlaneProjectName,
            projectIdentifier: config2.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: ticketId,
            extraSlug: null,
            timestamp: DateTimeOffset.Now);

        // --debug: capture worker stdin/stdout/stderr/envelope into .build/sessions/<file-stem>/
        // The stem is shared with the JSONL event sink so the two artifacts sort together.
        // Inside the JSONL, the SessionId field still carries the GUID as the correlation key.
        // Create the dir eagerly so the "Debug capture:" line at exit always points somewhere
        // real, even when the phase fails before the worker spawns (e.g. early git errors).
        string? debugCaptureDir = debugMode
            ? Path.GetFullPath(Path.Combine(cwd, ".build", "sessions", fileStem))
            : null;
        if (debugCaptureDir is not null)
            Directory.CreateDirectory(debugCaptureDir);

        await using var jsonlEventSink = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = sessionId,
            FileNameStem = fileStem
        }, sessionContext);
        // Digest is default-on when stderr is a TTY and the user has not opted out
        // via --quiet or replaced it with the --debug raw firehose. When stderr is
        // redirected (e.g. 2>err.log or piped to tee), the digest is suppressed to
        // keep scripted/CI logs clean unless BUILD_PROGRESS=1 forces it on.
        bool enableDigest = !debugMode
            && !quietMode
            && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1");

        var eventSink = new RecordingEventSink(jsonlEventSink);
        var buildOptions = new BuildOptions(
            SessionId: sessionId,
            WorkerName: config2.Workers.DefaultAgent,
            WorkerTimeout: TimeSpan.FromMinutes(config2.Workers.TimeoutMinutes),
            DebugCaptureDirectory: debugCaptureDir,
            LiveStdoutSink: debugMode ? Console.Out : null,
            LiveStderrSink: debugMode ? Console.Error : null,
            ProgressDigestSink: enableDigest ? Console.Error : null);

        string PlaneUrl() => BuildPlaneUrl(config2.Ticketing.PlaneBaseUrl, config2.Ticketing.PlaneWorkspaceSlug, ticketId);
        string? ArtifactsPath() => debugCaptureDir is not null
            ? $".build/sessions/{fileStem}/"
            : $".build/events/{fileStem}.jsonl";

        if (verb == "plan")
    {
        var phase = new PlanPhase(ticketing, workerFactory.Create(EffectiveAgentFor("plan")), eventSink, buildOptions, project: config2.Project);
        PlanResult result;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            result = await phase.RunAsync(ticketId, cwd, cts.Token);
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Ticket not found: {ex.Message}");
            if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
            dispatchExitCode = 2;
            break;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            dispatchExitCode = 1;
            break;
        }

        Ticket? planTicket = null;
        try { planTicket = await ticketing.GetAsync(ticketId, CancellationToken.None); }
        catch { /* best effort - summary tolerates a missing ticket */ }

        var planSummary = PhaseSummaryBuilder.BuildPlan(
            ticketId: result.TicketId,
            success: result.Success,
            riskLabel: result.RiskLabel,
            sizeLabel: result.SizeLabel,
            plannedAtSha: result.PlannedAtSha,
            failureReason: result.FailureReason,
            ticket: planTicket,
            events: eventSink.Snapshot(),
            sessionId: sessionId,
            planeUrl: PlaneUrl(),
            sessionArtifactsPath: ArtifactsPath());
        WriteSummary(planSummary);

        if (!result.Success)
        {
            Console.Error.WriteLine($"Plan phase failed: {result.FailureReason}");
            if (debugCaptureDir is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            dispatchExitCode = 1;
            break;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        dispatchExitCode = 0;
        break;
    }
    else if (verb == "implement")
    {
        var phase = new ImplementPhase(ticketing, workerFactory.Create(EffectiveAgentFor("implement")), eventSink, buildOptions, project: config2.Project);
        ImplementResult result;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            result = await phase.RunAsync(ticketId, cwd, cts.Token);
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Ticket not found: {ex.Message}");
            if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
            dispatchExitCode = 2;
            break;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            dispatchExitCode = 1;
            break;
        }

        // Best-effort git facts for the implement summary - failures are silently
        // absorbed and the summary just omits those fields.
        IReadOnlyList<DiffEntry> implDiff = Array.Empty<DiffEntry>();
        IReadOnlyList<string> implCommits = Array.Empty<string>();
        int implCommitCount = 0;
        if (result.Success && !string.IsNullOrEmpty(result.BranchName))
        {
            try
            {
                var (baseRef, _) = await ThroughlineBuild.Git.BaseRefResolver.ResolveAsync(summaryGit, cwd, CancellationToken.None);
                var d = await summaryGit.DiffAsync(baseRef, result.BranchName!, cwd, includePatchContent: false, CancellationToken.None);
                implDiff = d.Entries;
                implCommitCount = await summaryGit.RevListCountAsync($"{baseRef}..{result.BranchName}", cwd, CancellationToken.None);
                implCommits = await summaryGit.LogOnelineAsync($"{baseRef}..{result.BranchName}", 10, cwd, CancellationToken.None);
            }
            catch { /* tolerated */ }
        }

        var implSummary = PhaseSummaryBuilder.BuildImplement(
            ticketId: result.TicketId,
            success: result.Success,
            branchName: result.BranchName,
            commitSha: result.CommitSha,
            failureReason: result.FailureReason,
            events: eventSink.Snapshot(),
            diff: implDiff,
            commitOnelines: implCommits,
            commitCount: implCommitCount,
            planeUrl: PlaneUrl(),
            sessionArtifactsPath: ArtifactsPath());
        WriteSummary(implSummary);

        if (!result.Success)
        {
            Console.Error.WriteLine($"Implement phase failed: {result.FailureReason}");
            if (debugCaptureDir is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            dispatchExitCode = 1;
            break;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        dispatchExitCode = 0;
        break;
    }
    else if (verb == "review")
    {
        var verifierWorkerOptions = new WorkerOptions(
            TimeSpan.FromMinutes(config2.Review.VerifierTimeoutMinutes),
            config2.Review.VerifierAllowedTools,
            DebugCaptureDirectory: debugCaptureDir,
            LiveStdoutSink: debugMode ? Console.Out : null,
            LiveStderrSink: debugMode ? Console.Error : null,
            ProgressDigestSink: enableDigest ? Console.Error : null);
        var reviewOptions = new ReviewOptions(config2.Review.Checks, verifierWorkerOptions);
        var phase = new ReviewPhase(ticketing, workerFactory.Create(EffectiveAgentFor("review")), eventSink, buildOptions, reviewOptions, project: config2.Project);
        ReviewResult result;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            result = await phase.RunAsync(ticketId, cwd, cts.Token);
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Ticket not found: {ex.Message}");
            if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
            dispatchExitCode = 2;
            break;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            dispatchExitCode = 1;
            break;
        }

        var reviewSummary = PhaseSummaryBuilder.BuildReview(
            ticketId: result.TicketId,
            success: result.Success,
            verdict: result.Verdict?.ToString(),
            rationale: result.VerdictRationale,
            checksFailed: result.ChecksFailed,
            failureReason: result.FailureReason,
            events: eventSink.Snapshot(),
            planeUrl: PlaneUrl(),
            sessionArtifactsPath: ArtifactsPath());
        WriteSummary(reviewSummary);

        if (!result.Success)
        {
            Console.Error.WriteLine($"Review phase failed: {result.FailureReason}");
            if (debugCaptureDir is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            dispatchExitCode = 1;
            break;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        dispatchExitCode = 0;
        break;
    }
    else if (verb == "ship")
    {
        var shipOptions = new ShipOptions(
            RegressionChecks: config2.Ship.RegressionChecks,
            Remote: config2.Ship.Remote,
            BaseBranch: config2.Ship.BaseBranch,
            DeleteFeatureBranch: config2.Ship.DeleteFeatureBranch,
            NoAutoMerge: noAutoMerge);
        var gitClient = new ProcessGitClient(cwd);
        var checksRunner = new AutomatedChecksRunner();
        var phase = new ShipPhase(ticketing, eventSink, buildOptions, shipOptions, gitClient: gitClient, checksRunner: checksRunner);
        ShipResult result;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            result = await phase.RunAsync(ticketId, cwd, cts.Token);
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Ticket not found: {ex.Message}");
            if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
            dispatchExitCode = 2;
            break;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            dispatchExitCode = 1;
            break;
        }

        // Derive branch name from the deterministic worktree layout (best effort).
        string shipBranchName = "(unknown)";
        try
        {
            using var fetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ticket = await ticketing.GetAsync(ticketId, fetchCts.Token);
            var layout = PhaseWorktreeLayout.Compute(ticketId, ticket.Title, cwd);
            shipBranchName = layout.BranchName;
        }
        catch { /* tolerated */ }

        IReadOnlyList<DiffEntry> shipDiff = Array.Empty<DiffEntry>();
        if (result.Success && !string.IsNullOrEmpty(result.MergedSha))
        {
            try
            {
                // For a ship that just fast-forwarded, diff is most informative when
                // measured against the merge-base. When no remote is configured, fall
                // back to the local base branch (TLB-127).
                string ontoRef;
                var summaryRemoteExists = await summaryGit.RemoteExistsAsync(config2.Ship.Remote, cwd, CancellationToken.None);
                if (summaryRemoteExists)
                    ontoRef = $"{config2.Ship.Remote}/{config2.Ship.BaseBranch}";
                else
                    ontoRef = config2.Ship.BaseBranch;
                var d = await summaryGit.DiffAsync(ontoRef, result.MergedSha!, cwd, includePatchContent: false, CancellationToken.None);
                shipDiff = d.Entries;
            }
            catch { /* tolerated */ }
        }

        var shipSummary = PhaseSummaryBuilder.BuildShip(
            ticketId: result.TicketId,
            success: result.Success,
            branchName: shipBranchName == "(unknown)" ? null : shipBranchName,
            mergedSha: result.MergedSha,
            failureReason: result.FailureReason,
            failedAtStage: result.FailedAt?.ToString(),
            events: eventSink.Snapshot(),
            diff: shipDiff,
            planeUrl: PlaneUrl(),
            sessionArtifactsPath: ArtifactsPath());
        WriteSummary(shipSummary);

        if (result.Success)
        {
            dispatchExitCode = 0;
            break;
        }

        // Map FailedAt to exit code: gate failures -> 1, infrastructure failures -> 4.
        var failedAt = result.FailedAt;
        var stageName = failedAt?.ToString().ToLowerInvariant() ?? "unknown";
        Console.Error.WriteLine($"Ship blocked at {stageName}: {result.FailureReason}");

        dispatchExitCode = failedAt switch
        {
            ShipFailureStage.Rebase => 1,
            ShipFailureStage.ConflictMarkerScan => 1,
            ShipFailureStage.RegressionChecks => 1,
            ShipFailureStage.StateCheck => 4,
            ShipFailureStage.Fetch => 4,
            ShipFailureStage.FastForwardMerge => 4,
            ShipFailureStage.Decruft => 0,  // decruft failure is post-success non-fatal
            _ => 1
        };
        break;
    }
    else if (verb == "chain")
    {
        // Collect additional positional ticket IDs beyond args[1] (args[0] is the verb).
        var extraTicketIds = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                extraTicketIds.Add(args[i]);
        }

        // Construct per-phase factories for ChainPhase.
        var planPhaseFactory = (BuildOptions buildOpts) =>
            new PlanPhase(ticketing, workerFactory.Create(EffectiveAgentFor("plan")), eventSink, buildOpts, project: config2.Project);

        var implementPhaseFactory = (BuildOptions buildOpts, ImplementPhaseOptions implOpts) =>
            new ImplementPhase(ticketing, workerFactory.Create(EffectiveAgentFor("implement")), eventSink, buildOpts, project: config2.Project, phaseOptions: implOpts);

        var reviewPhaseFactory = (BuildOptions buildOpts) =>
        {
            var verifierWorkerOptions = new WorkerOptions(
                TimeSpan.FromMinutes(config2.Review.VerifierTimeoutMinutes),
                config2.Review.VerifierAllowedTools,
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode ? Console.Out : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null);
            var reviewOptions = new ReviewOptions(config2.Review.Checks, verifierWorkerOptions);
            return new ReviewPhase(ticketing, workerFactory.Create(EffectiveAgentFor("review")), eventSink, buildOpts, reviewOptions, project: config2.Project);
        };

        var shipPhaseFactory = (BuildOptions buildOpts) =>
        {
            var shipOptions = new ShipOptions(
                RegressionChecks: config2.Ship.RegressionChecks,
                Remote: config2.Ship.Remote,
                BaseBranch: config2.Ship.BaseBranch,
                DeleteFeatureBranch: config2.Ship.DeleteFeatureBranch,
                NoAutoMerge: noAutoMerge);
            var gitClient = new ProcessGitClient(cwd);
            var checksRunner = new AutomatedChecksRunner();
            return new ShipPhase(ticketing, eventSink, buildOpts, shipOptions, gitClient: gitClient, checksRunner: checksRunner);
        };

        var ratifierFactory = (BuildOptions _) =>
        {
            var ratifierWorkerOptions = new WorkerOptions(
                TimeSpan.FromMinutes(config2.Review.VerifierTimeoutMinutes),
                config2.Review.VerifierAllowedTools,
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode ? Console.Out : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null);
            return (IObsoleteRatifier)new ObsoleteRatifier(
                workerFactory.Create(EffectiveAgentFor("review")),
                ratifierWorkerOptions,
                cwd,
                git: new ProcessGitClient(cwd));
        };

        var chainPhase = new ChainPhase(
            ticketing,
            eventSink,
            buildOptions,
            planPhaseFactory,
            implementPhaseFactory,
            reviewPhaseFactory,
            shipPhaseFactory,
            workingDirectory: cwd,
            ratifierFactory: ratifierFactory);

        // Multi-ticket path: if additional positional IDs were supplied, use ParallelDispatcher.
        if (extraTicketIds.Count > 0)
        {
            var allTicketIds = new List<string> { ticketId };
            allTicketIds.AddRange(extraTicketIds);

            // Fetch tickets and build dependency graph from blocked_by relations.
            IReadOnlyList<ThroughlineBuild.Contracts.Models.Ticket> batchTickets;
            try
            {
                using var batchCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; batchCts.Cancel(); };
                batchTickets = await ticketing.GetBatchAsync(allTicketIds, batchCts.Token).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                Console.Error.WriteLine($"Ticket not found: {ex.Message}");
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }

            var ticketIdSet = new HashSet<string>(allTicketIds, StringComparer.OrdinalIgnoreCase);
            var graph = new ThroughlineBuild.Phases.TicketGraph();
            foreach (var id in allTicketIds)
                graph.AddNode(id);

            // Add edges for blocked_by relations that are within the dispatched set.
            try
            {
                using var relCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; relCts.Cancel(); };
                foreach (var id in allTicketIds)
                {
                    var relations = await ticketing.GetRelationsAsync(id, relCts.Token).ConfigureAwait(false);
                    foreach (var rel in relations)
                    {
                        if (rel.Kind == "blocked_by" && ticketIdSet.Contains(rel.TargetId))
                            graph.AddEdge(rel.TargetId, id); // TargetId blocks id
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }

            var dispatcher = new ThroughlineBuild.Phases.ParallelDispatcher(
                chainPhase,
                eventSink,
                config2.Workers.MaxConcurrency);

            var baseChainOptions = new ThroughlineBuild.Phases.ChainPhaseOptions(
                TicketId: ticketId,
                Debug: debugMode,
                NoAutoResolve: noAutoResolve);

            ThroughlineBuild.Contracts.Models.ParallelDispatchResult dispatchResult;
            try
            {
                using var dispatchCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; dispatchCts.Cancel(); };
                dispatchResult = await dispatcher.RunAsync(allTicketIds, graph, baseChainOptions, dispatchCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }

            // Print per-ticket summary
            foreach (var r in dispatchResult.Results)
            {
                var durationMs = (long)r.TotalDuration.TotalMilliseconds;
                Console.WriteLine($"[{r.TicketId}] {r.Outcome} ({durationMs}ms)");
            }

            return dispatchResult.Success ? 0 : 1;
        }

        // Single-ticket path (original behavior).
        var chainRunner = new DefaultChainRunner(chainPhase);
        var chainCommand = new ChainCommand(chainRunner, ticketing);

        // Collect all ticket IDs from args: args[1] is the primary, plus any additional
        // positional args that don't start with '--'.
        var chainTicketIds = new List<string> { ticketId };
        for (int i = 2; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                chainTicketIds.Add(args[i]);
        }

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            if (chainTicketIds.Count > 1)
            {
                // Sequential fallback for multi-ticket dispatch.
                // TLB-312 will replace this with concurrent ParallelDispatcher when rebased.
                var allResults = await SequentialChainDispatcher.RunAsync(
                    chainTicketIds,
                    async (tid, token) =>
                    {
                        var singleCtx = new TicketCommandContext(tid, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["debug"] = debugMode ? "true" : "false",
                            ["no-auto-resolve"] = noAutoResolve ? "true" : "false"
                        });
                        var singleCommand = new ChainCommand(chainRunner, ticketing);
                        await singleCommand.ExecuteAsync(singleCtx, token).ConfigureAwait(false);
                        return singleCommand.LastChainResult ?? new ChainResult(
                            TicketId: tid,
                            Steps: Array.Empty<ChainStep>(),
                            Outcome: ChainOutcome.StoppedAtPlan,
                            TotalDuration: TimeSpan.Zero,
                            FinalRationale: "command returned no result");
                    },
                    continuePastFailure,
                    cts.Token).ConfigureAwait(false);

                ChainCommand.PrintAggregateReport(allResults);

                // Exit 0 if all results are success or skipped; non-zero if any failed.
                bool allGood = allResults.All(r =>
                    r.Outcome == ChainOutcome.Completed
                    || r.Outcome == ChainOutcome.RatifiedObsolete
                    || r.Outcome == ChainOutcome.ParentCompleted
                    || r.Outcome == ChainOutcome.Skipped);
                return allGood ? 0 : 1;
            }

            // Single-ticket path.
            var chainCtx = new TicketCommandContext(ticketId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["debug"] = debugMode ? "true" : "false",
                ["no-auto-resolve"] = noAutoResolve ? "true" : "false"
            });
            var cmdResult = await chainCommand.ExecuteAsync(chainCtx, cts.Token).ConfigureAwait(false);

            if (!cmdResult.Success)
            {
                // Map ChainResult.Outcome to exit code.
                if (chainCommand.LastChainResult is not null)
                {
                    dispatchExitCode = chainCommand.LastChainResult.Outcome switch
                    {
                        ChainOutcome.Completed => 0,
                        ChainOutcome.RatifiedObsolete => 0,
                        ChainOutcome.ParentCompleted => 0,
                        ChainOutcome.RefusedInitialState => 2,
                        ChainOutcome.ParentHasGrandchildren => 2,
                        ChainOutcome.StoppedAtPlan => 3,
                        ChainOutcome.ParentStoppedEarly => 3,
                        ChainOutcome.Skipped => 3,
                        ChainOutcome.StoppedAtImplement => 4,
                        ChainOutcome.StoppedAtReview => 5,
                        ChainOutcome.ReworkCapExceeded => 6,
                        ChainOutcome.StoppedAtShip => 7,
                        _ => 1
                    };
                    break;
                }
                // Unhandled exception path: LastChainResult not set because ChainCommand
                // caught an exception before completing the chain. Print the message so
                // the operator can see what went wrong instead of a silent exit code 1.
                if (!string.IsNullOrEmpty(cmdResult.Message))
                    Console.Error.WriteLine($"Error: {cmdResult.Message}");
                dispatchExitCode = 1;
                break;
            }

            dispatchExitCode = 0;
            break;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            dispatchExitCode = 1;
            break;
        }
    }
    }

    // Multi-ticket verbs dispatch complete; rework and decompose are single-ticket only.
    if (dispatchExitCode != 0)
        return dispatchExitCode;

    // Single-ticket verbs: rework, decompose (require per-ticket setup)
    if (verb == "rework" || verb == "decompose")
    {
        var singleTicketId = ticketIds[0];
        var sessionId = Guid.NewGuid().ToString("N");
        var fileStem = SessionFileNameBuilder.Build(
            projectName: config2.Ticketing.PlaneProjectName,
            projectIdentifier: config2.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: singleTicketId,
            extraSlug: null,
            timestamp: DateTimeOffset.Now);

        string? debugCaptureDir = debugMode
            ? Path.GetFullPath(Path.Combine(cwd, ".build", "sessions", fileStem))
            : null;
        if (debugCaptureDir is not null)
            Directory.CreateDirectory(debugCaptureDir);

        await using var jsonlEventSink = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = sessionId,
            FileNameStem = fileStem
        }, sessionContext);
        bool enableDigest = !debugMode
            && !quietMode
            && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1");

        var eventSink = new RecordingEventSink(jsonlEventSink);
        var buildOptions = new BuildOptions(
            SessionId: sessionId,
            WorkerName: config2.Workers.DefaultAgent,
            WorkerTimeout: TimeSpan.FromMinutes(config2.Workers.TimeoutMinutes),
            DebugCaptureDirectory: debugCaptureDir,
            LiveStdoutSink: debugMode ? Console.Out : null,
            LiveStderrSink: debugMode ? Console.Error : null,
            ProgressDigestSink: enableDigest ? Console.Error : null);

        string PlaneUrl() => BuildPlaneUrl(config2.Ticketing.PlaneBaseUrl, config2.Ticketing.PlaneWorkspaceSlug, singleTicketId);
        string? ArtifactsPath() => debugCaptureDir is not null
            ? $".build/sessions/{fileStem}/"
            : $".build/events/{fileStem}.jsonl";

        if (verb == "rework")
        {
            // Parse --feedback "text" from remaining args.
            string? feedbackText = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--feedback" && i + 1 < args.Length)
                {
                    feedbackText = args[i + 1];
                    i++; // skip value
                }
            }

            var reworkPhaseOptions = new ReworkPhaseOptions(
                TicketId: singleTicketId,
                ManualFeedback: feedbackText,
                ReworkRoundNumber: 1,
                Debug: debugMode);

            var retriever = new ReviewFeedbackRetriever(ResolveLogDir(config2.Events.LogDirectory));

            var reworkPhase = new ReworkPhase(
                ticketing,
                workerFactory.Create(EffectiveAgentFor("implement")),
                eventSink,
                buildOptions,
                retriever,
                reworkPhaseOptions,
                gitClient: new ThroughlineBuild.Git.ProcessGitClient(cwd),
                project: config2.Project);

        var reworkRunner = new DefaultReworkRunner(reworkPhase, cwd);
        var reworkCommand = new ReworkCommand(reworkRunner, cwd);

        var reworkArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["debug"] = debugMode ? "true" : "false"
        };
        if (feedbackText is not null)
            reworkArgs["feedback"] = feedbackText;

        var reworkCtx = new TicketCommandContext(singleTicketId, reworkArgs);

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var cmdResult = await reworkCommand.ExecuteAsync(reworkCtx, cts.Token).ConfigureAwait(false);

            if (reworkCommand.LastReworkResult is not null)
            {
                return reworkCommand.LastReworkResult.Outcome switch
                {
                    ReworkOutcome.Implemented => 0,
                    ReworkOutcome.TicketNotInProgress => 2,
                    ReworkOutcome.NoFeedbackAvailable => 3,
                    ReworkOutcome.ImplementFailed => 4,
                    _ => 1
                };
            }

            return cmdResult.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        }
        else if (verb == "decompose")
        {
        Ticket ticket;
        try
        {
            ticket = await ticketing.GetAsync(singleTicketId, CancellationToken.None);
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Ticket not found: {ex.Message}");
            if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(ex));
            return 2;
        }

        var phase = new DecomposePhase(ticketing, workerFactory.Create(EffectiveAgentFor("decompose")), eventSink, buildOptions);
        DecomposeResult result;
        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            result = await phase.RunAsync(singleTicketId, cwd, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }

        var childSizes = result.ChildSpecs is not null
            ? result.ChildSpecs.Select(cs => cs.Size).ToList()
            : new List<string>();
        var childSizesReadOnly = (IReadOnlyList<string>)childSizes;

        var decomposeSummary = PhaseSummaryBuilder.BuildDecompose(
            ticketId: result.TicketId,
            success: result.Success,
            failureReason: result.FailureReason,
            createdIds: result.CreatedIds ?? Array.Empty<string>(),
            childSizes: childSizesReadOnly,
            events: eventSink.Snapshot(),
            planeUrl: PlaneUrl(),
            sessionArtifactsPath: ArtifactsPath());
        WriteSummary(decomposeSummary);

        if (!result.Success)
        {
            Console.Error.WriteLine($"Decompose phase failed: {result.FailureReason}");
            if (debugCaptureDir is not null)
                Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            return 1;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        return 0;
        }
    }

    // Fallback: should not reach here if all verbs are handled
    return 0;
}

// Builds the Plane work-item deep-link URL using the ?next_path= redirect parameter.
// Returns an empty string when any argument is unset so callers can omit the URL line.
static string BuildPlaneUrl(string planeBaseUrl, string workspaceSlug, string ticketId)
{
    if (string.IsNullOrEmpty(planeBaseUrl) || string.IsNullOrEmpty(workspaceSlug) || string.IsNullOrEmpty(ticketId))
        return string.Empty;
    return $"{planeBaseUrl.TrimEnd('/')}/?next_path=/{workspaceSlug}/browse/{ticketId}";
}

// Returns the first line of ex.StackTrace, trimmed, or "(no stack trace)" when absent.
// Method names are always available in AOT; source line numbers require embedded PDB symbols.
static string FirstExceptionFrame(Exception ex)
{
    var trace = ex.StackTrace;
    if (string.IsNullOrEmpty(trace)) return "(no stack trace)";
    return trace.TrimStart().Split('\n')[0].Trim();
}

static string? WireUpConditionalCommands(
    string verb,
    TicketCommandRegistry registry,
    BuildSecrets secrets,
    LlmConfig llmConfig,
    HttpClient http,
    ITicketing ticketing,
    IEventSink eventSink,
    string mainWorktreePath)
{
    if (verb != "close" && verb != "defer" && verb != "reopen")
        return null;

    ILlmClient llmClient;
    try
    {
        llmClient = LlmClientFactory.Create(llmConfig, secrets, http);
    }
    catch (ConfigException ex)
    {
        return ex.Message;
    }
    var translator = new ReasonTranslator(llmClient);

    if (verb == "close")
    {
        var gitClient = new ProcessGitClient(mainWorktreePath);
        var decrufter = new WorktreeDecrufter(gitClient);
        registry.Register("close", new CloseCommand(
            ticketing,
            eventSink,
            gitClient,
            translator,
            decrufter,
            mainWorktreePath));
    }
    else if (verb == "defer")
    {
        var gitClient = new ProcessGitClient(mainWorktreePath);
        var decrufter = new WorktreeDecrufter(gitClient);
        registry.Register("defer", new DeferCommand(
            ticketing,
            eventSink,
            gitClient,
            translator,
            decrufter,
            mainWorktreePath));
    }
    else if (verb == "reopen")
    {
        registry.Register("reopen", new ReopenCommand(
            ticketing,
            eventSink,
            translator));
    }

    return null;
}

