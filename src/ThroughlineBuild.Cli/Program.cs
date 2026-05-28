using System.Net.Http;
using System.Reflection;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Scaffold;
using ThroughlineBuild.Verification;
using ThroughlineBuild.Workers.ClaudeCode;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
    {
        Console.WriteLine(CliUsage.UsageText);
        return 0;
    }

    // Pre-pass: strip --debug, --quiet, and --summary-json from args before any positional parser sees them.
    // All three are bare bool flags (no value); the existing key/value parser expects pairs
    // and would mangle subsequent args if they were left in.
    bool debugMode = false;
    bool quietMode = false;
    bool summaryJson = false;
    var filteredArgs = new List<string>(args.Length);
    foreach (var a in args)
    {
        if (a == "--debug")
            debugMode = true;
        else if (a == "--quiet")
            quietMode = true;
        else if (a == "--summary-json")
            summaryJson = true;
        else
            filteredArgs.Add(a);
    }
    args = filteredArgs.ToArray();

    var verb = args[0];

    // Arg validation for phase verbs happens BEFORE config load so a missing id
    // surfaces a usage error (exit 2) rather than a config-not-found error.
    if (verb == "plan" || verb == "implement" || verb == "review" || verb == "ship" || verb == "chain" || verb == "rework")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
            return 2;
        }

        // For chain: reject multiple positional ticket IDs (v1 out of scope).
        if (verb == "chain" && args.Length > 2)
        {
            // Check if args[2] is a flag or another ticket ID.
            if (!args[2].StartsWith("--"))
            {
                Console.Error.WriteLine("Error: build chain accepts exactly one ticket ID in v1; multi-ticket dispatch is planned for a future release.");
                return 2;
            }
        }

        // For rework: reject multiple positional ticket IDs (single ticket only).
        if (verb == "rework" && args.Length > 2)
        {
            if (!args[2].StartsWith("--"))
            {
                Console.Error.WriteLine("Error: build rework accepts exactly one ticket ID; multi-ticket dispatch is not supported.");
                return 2;
            }
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
        return 3;
    }

    string ResolveLogDir(string raw) => Path.GetFullPath(BuildConfigLoader.ResolveLogDirectory(configPath2, raw, cwd2));

    var buildVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    var sessionContext = new SessionContext(
        ProjectId: config2.Ticketing.PlaneProjectId,
        ProjectName: string.IsNullOrEmpty(config2.Ticketing.PlaneProjectName) ? null : config2.Ticketing.PlaneProjectName,
        WorkspaceSlug: config2.Ticketing.PlaneWorkspaceSlug,
        BuildVersion: buildVersion);

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

        var wireUpError = WireUpConditionalCommands(verb, registry, secrets2, http2, ticketing2, eventSink2, cwd2);
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
        for (int i = parseStart; i + 1 < args.Length; i += 2)
        {
            var key = args[i];
            if (key.StartsWith("--"))
                key = key.Substring(2);
            extraArgs[key] = args[i + 1];
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
            var cmd2 = new NewCommand(newPhase2, config2.Project.PlaneProjectUrl, buildOptions2.DebugCaptureDirectory);
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
            var cmd2 = new NewCommand(newPhase2, config2.Project.PlaneProjectUrl, buildOptions2.DebugCaptureDirectory);
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
                    Model = config2.Llm.DefaultModel,
                    DefaultModel = config2.Llm.DefaultModel
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
            var cmd3 = new NewCommand(newPhase2, config2.Project.PlaneProjectUrl, buildOptions2.DebugCaptureDirectory);
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
            // --debug already stripped by pre-pass; other unknown flags are silently ignored
        }

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

    if (verb != "plan" && verb != "implement" && verb != "review" && verb != "ship" && verb != "chain" && verb != "rework")
    {
        Console.Error.WriteLine($"Unknown subcommand: {verb}");
        Console.Error.WriteLine(CliUsage.UsageText);
        return 2;
    }

    var ticketId = args[1];
    var cwd = resolvedCwd;

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
    var allAgentNames = new HashSet<string>(StringComparer.Ordinal) { config2.Workers.DefaultAgent };
    foreach (var phaseName in config2.Workers.Phases.Values)
        allAgentNames.Add(phaseName);

    var factoryEntries = new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal);
    foreach (var agentName in allAgentNames)
    {
        if (!config2.Workers.Agents.TryGetValue(agentName, out var aCfg))
            throw new ConfigException($"missing [workers.{agentName}] sub-table in config");
        var capturedCfg = aCfg;
        factoryEntries[agentName] = () => new ClaudeCodeAgent(new ClaudeCodeOptions
        {
            ExecutablePath = capturedCfg.Executable,
            MaxOutputTokens = capturedCfg.MaxOutputTokens,
            Model = config2.Llm.DefaultModel,
            DefaultModel = config2.Llm.DefaultModel
        });
    }
    var workerFactory = new WorkerAgentFactory(factoryEntries);

    // Helper: resolve the agent name for a given phase, falling back to default_agent.
    string AgentFor(string phase) =>
        config2.Workers.Phases.TryGetValue(phase, out var n) ? n : config2.Workers.DefaultAgent;
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

    // Shared git client (read-only ops only at this layer) for summary-block construction.
    var summaryGit = new ProcessGitClient(cwd);
    string PlaneUrl() => BuildPlaneUrl(config2.Project.PlaneProjectUrl, ticketId);
    string? ArtifactsPath() => debugCaptureDir is not null
        ? $".build/sessions/{fileStem}/"
        : $".build/events/{fileStem}.jsonl";

    void WriteSummary(PhaseSummary summary)
    {
        var text = summaryJson
            ? PhaseSummaryRenderer.RenderJson(summary)
            : PhaseSummaryRenderer.RenderText(summary);
        Console.Out.Write(text);
        if (!text.EndsWith('\n')) Console.Out.WriteLine();
    }

    if (verb == "plan")
    {
        var phase = new PlanPhase(ticketing, workerFactory.Create(AgentFor("plan")), eventSink, buildOptions, project: config2.Project);
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
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
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
            return 1;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        return 0;
    }
    else if (verb == "implement")
    {
        var phase = new ImplementPhase(ticketing, workerFactory.Create(AgentFor("implement")), eventSink, buildOptions, project: config2.Project);
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
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
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
            return 1;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
        return 0;
    }
    else if (verb == "ship")
    {
        var shipOptions = new ShipOptions(
            RegressionChecks: config2.Ship.RegressionChecks,
            Remote: config2.Ship.Remote,
            BaseBranch: config2.Ship.BaseBranch,
            DeleteFeatureBranch: config2.Ship.DeleteFeatureBranch);
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
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
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
            return 0;
        }

        // Map FailedAt to exit code: gate failures -> 1, infrastructure failures -> 4.
        var failedAt = result.FailedAt;
        var stageName = failedAt?.ToString().ToLowerInvariant() ?? "unknown";
        Console.Error.WriteLine($"Ship blocked at {stageName}: {result.FailureReason}");

        return failedAt switch
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
    }
    else if (verb == "chain")
    {
        // Construct per-phase factories for ChainPhase.
        var planPhaseFactory = (BuildOptions buildOpts) =>
            new PlanPhase(ticketing, workerFactory.Create(AgentFor("plan")), eventSink, buildOpts, project: config2.Project);

        var implementPhaseFactory = (BuildOptions buildOpts, ImplementPhaseOptions implOpts) =>
            new ImplementPhase(ticketing, workerFactory.Create(AgentFor("implement")), eventSink, buildOpts, project: config2.Project);

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
            return new ReviewPhase(ticketing, workerFactory.Create(AgentFor("review")), eventSink, buildOpts, reviewOptions, project: config2.Project);
        };

        var shipPhaseFactory = (BuildOptions buildOpts) =>
        {
            var shipOptions = new ShipOptions(
                RegressionChecks: config2.Ship.RegressionChecks,
                Remote: config2.Ship.Remote,
                BaseBranch: config2.Ship.BaseBranch,
                DeleteFeatureBranch: config2.Ship.DeleteFeatureBranch);
            var gitClient = new ProcessGitClient(cwd);
            var checksRunner = new AutomatedChecksRunner();
            return new ShipPhase(ticketing, eventSink, buildOpts, shipOptions, gitClient: gitClient, checksRunner: checksRunner);
        };

        var chainPhase = new ChainPhase(
            ticketing,
            eventSink,
            buildOptions,
            planPhaseFactory,
            implementPhaseFactory,
            reviewPhaseFactory,
            shipPhaseFactory,
            workingDirectory: cwd);

        var chainRunner = new DefaultChainRunner(chainPhase);
        var chainCommand = new ChainCommand(chainRunner, ticketing, config2.Project.PlaneProjectUrl);
        var chainCtx = new TicketCommandContext(ticketId, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["debug"] = debugMode ? "true" : "false"
        });

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var cmdResult = await chainCommand.ExecuteAsync(chainCtx, cts.Token).ConfigureAwait(false);

            if (!cmdResult.Success)
            {
                // Map ChainResult.Outcome to exit code.
                if (chainCommand.LastChainResult is not null)
                {
                    return chainCommand.LastChainResult.Outcome switch
                    {
                        ChainOutcome.Completed => 0,
                        ChainOutcome.RefusedInitialState => 2,
                        ChainOutcome.StoppedAtPlan => 3,
                        ChainOutcome.StoppedAtImplement => 4,
                        ChainOutcome.StoppedAtReview => 5,
                        ChainOutcome.ReworkCapExceeded => 6,
                        ChainOutcome.StoppedAtShip => 7,
                        _ => 1
                    };
                }
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
    else if (verb == "rework")
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
            TicketId: ticketId,
            ManualFeedback: feedbackText,
            ReworkRoundNumber: 1,
            Debug: debugMode);

        var retriever = new ReviewFeedbackRetriever(ResolveLogDir(config2.Events.LogDirectory));

        var reworkPhase = new ReworkPhase(
            ticketing,
            workerFactory.Create(AgentFor("implement")),
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

        var reworkCtx = new TicketCommandContext(ticketId, reworkArgs);

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
    else // review
    {
        var verifierWorkerOptions = new WorkerOptions(
            TimeSpan.FromMinutes(config2.Review.VerifierTimeoutMinutes),
            config2.Review.VerifierAllowedTools,
            DebugCaptureDirectory: debugCaptureDir,
            LiveStdoutSink: debugMode ? Console.Out : null,
            LiveStderrSink: debugMode ? Console.Error : null,
            ProgressDigestSink: enableDigest ? Console.Error : null);
        var reviewOptions = new ReviewOptions(config2.Review.Checks, verifierWorkerOptions);
        var phase = new ReviewPhase(ticketing, workerFactory.Create(AgentFor("review")), eventSink, buildOptions, reviewOptions, project: config2.Project);
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
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
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
            return 4;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");

        return result.Verdict == ThroughlineBuild.Contracts.Models.VerdictKind.Pass ? 0 : 1;
    }
}

// Builds the Plane work-item URL by joining the configured PlaneProjectUrl with the
// ticket id. Returns an empty string when the config field is unset so the summary
// renderer can omit the URL line entirely.
static string BuildPlaneUrl(string planeProjectUrl, string ticketId)
{
    if (string.IsNullOrEmpty(planeProjectUrl) || string.IsNullOrEmpty(ticketId))
        return string.Empty;
    var trimmed = planeProjectUrl.TrimEnd('/');
    return $"{trimmed}/browse/{ticketId}/";
}

static string? WireUpConditionalCommands(
    string verb,
    TicketCommandRegistry registry,
    BuildSecrets secrets,
    HttpClient http,
    ITicketing ticketing,
    IEventSink eventSink,
    string mainWorktreePath)
{
    if (verb != "close" && verb != "defer" && verb != "reopen")
        return null;

    if (string.IsNullOrEmpty(secrets.AnthropicApiKey))
    {
        return "anthropic api key required for close/defer/reopen (reason translation)";
    }

    var anthropicClient = new AnthropicClient(http, new AnthropicOptions { ApiKey = secrets.AnthropicApiKey });
    var translator = new ReasonTranslator(anthropicClient);

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

