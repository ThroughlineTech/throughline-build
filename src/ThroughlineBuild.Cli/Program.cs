using System.Net.Http;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.EventLog;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.ClaudeCode;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    const string Usage = """
build - Throughline Build

Usage:
  build plan <ticket-id>      Run the plan phase for a ticket
  build amend <ticket-id>     Amend an existing ticket
  build close <ticket-id>     Close a ticket
  build defer <ticket-id>     Defer a ticket
  build reopen <ticket-id>    Reopen a previously closed or deferred ticket
  build --help                Show this help

Exit codes:
  0  Success
  1  Phase or command failure
  2  Config error or unknown verb
  3  Missing secret (env var not set)
""";

    if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    var verb = args[0];

    var cwd2 = Directory.GetCurrentDirectory();

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

    var sessionId2 = Guid.NewGuid().ToString("N");
    var http2 = new HttpClient();
    var ticketing2 = new PlaneTicketingClient(http2, new PlaneClientOptions
    {
        BaseUrl = config2.Ticketing.PlaneBaseUrl,
        ApiToken = secrets2.PlaneApiToken,
        WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
        ProjectId = config2.Ticketing.PlaneProjectId,
        ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
    });
    await using var eventSink2 = new JsonlEventSink(new EventLogOptions
    {
        BaseDirectory = config2.Events.LogDirectory,
        SessionId = sessionId2
    });

    // Build the ticket-command registry. B05 wires the dispatch path; concrete
    // commands (amend/close/defer/reopen) get registered here as they land in
    // subsequent briefs (B06-B09).
    var registry = new TicketCommandRegistry();
    registry.Register("amend", new AmendCommand(ticketing2, eventSink2));

    if (verb == "close")
    {
        if (string.IsNullOrEmpty(secrets2.AnthropicApiKey))
        {
            Console.Error.WriteLine("Secret error: anthropic api key required for close (reason translation)");
            return 3;
        }
        var anthropicClient = new AnthropicClient(http2, new AnthropicOptions { ApiKey = secrets2.AnthropicApiKey });
        var translator = new ReasonTranslator(anthropicClient);
        var gitClient = new ProcessGitClient(cwd2);
        var decrufter = new WorktreeDecrufter(gitClient);
        registry.Register("close", new CloseCommand(
            ticketing2,
            eventSink2,
            gitClient,
            translator,
            decrufter,
            cwd2));
    }
    if (verb == "defer")
    {
        if (string.IsNullOrEmpty(secrets2.AnthropicApiKey))
        {
            Console.Error.WriteLine("Secret error: anthropic api key required for defer (reason translation)");
            return 3;
        }
        var anthropicClient = new AnthropicClient(http2, new AnthropicOptions { ApiKey = secrets2.AnthropicApiKey });
        var translator = new ReasonTranslator(anthropicClient);
        var gitClient = new ProcessGitClient(cwd2);
        var decrufter = new WorktreeDecrufter(gitClient);
        registry.Register("defer", new DeferCommand(
            ticketing2,
            eventSink2,
            gitClient,
            translator,
            decrufter,
            cwd2));
    }
    // registry.Register("reopen", new ReopenTicketCommand(...));

    if (verb == "amend" || verb == "close" || verb == "defer" || verb == "reopen")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
            return 2;
        }

        var verbTicketId = args[1];
        var extraArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        int parseStart = 2;
        // For 'close' and 'defer', accept reason as first positional arg after ticket-id.
        if ((verb == "close" || verb == "defer") && args.Length >= 3 && !args[2].StartsWith("--"))
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

    if (verb != "plan")
    {
        Console.Error.WriteLine($"Unknown subcommand: {verb}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.Error.WriteLine("Error: ticket-id is required");
        Console.Error.WriteLine("Usage: build plan <ticket-id>");
        return 2;
    }

    var ticketId = args[1];
    var cwd = Directory.GetCurrentDirectory();

    string configPath;
    try
    {
        configPath = BuildConfigLoader.FindConfigFile(cwd)
            ?? throw new ConfigException($"config file not found: searched from {cwd} upwards for .build/config.toml");
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return 2;
    }

    BuildConfig config;
    try
    {
        config = BuildConfigLoader.Load(configPath);
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return 2;
    }

    BuildSecrets secrets;
    try
    {
        secrets = BuildConfigLoader.ResolveSecrets(config);
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine($"Secret error: {ex.Message}");
        return 3;
    }

    var sessionId = Guid.NewGuid().ToString("N");
    var http = new HttpClient();
    var ticketing = new PlaneTicketingClient(http, new PlaneClientOptions
    {
        BaseUrl = config.Ticketing.PlaneBaseUrl,
        ApiToken = secrets.PlaneApiToken,
        WorkspaceSlug = config.Ticketing.PlaneWorkspaceSlug,
        ProjectId = config.Ticketing.PlaneProjectId,
        ProjectIdentifier = config.Ticketing.PlaneProjectIdentifier
    });
    var worker = new ClaudeCodeAgent(new ClaudeCodeOptions
    {
        ExecutablePath = config.Workers.ClaudeCodeExecutable
    });
    await using var eventSink = new JsonlEventSink(new EventLogOptions
    {
        BaseDirectory = config.Events.LogDirectory,
        SessionId = sessionId
    });
    var buildOptions = new BuildOptions(
        SessionId: sessionId,
        WorkerName: config.Workers.DefaultAgent,
        WorkerTimeout: TimeSpan.FromMinutes(config.Workers.TimeoutMinutes));

    var phase = new PlanPhase(ticketing, worker, eventSink, buildOptions);
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

    if (!result.Success)
    {
        Console.Error.WriteLine($"Plan phase failed: {result.FailureReason}");
        return 1;
    }

    Console.WriteLine($"Plan complete: {result.TicketId} risk={result.RiskLabel} size={result.SizeLabel}");
    return 0;
}
