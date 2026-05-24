using System.Net.Http;
using ThroughlineBuild.Anthropic;
using ThroughlineBuild.Cli;
using ThroughlineBuild.Commands;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.EventLog;
using ThroughlineBuild.Git;
using ThroughlineBuild.Helpers;
using ThroughlineBuild.JudgmentSlots;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.ClaudeCode;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
    {
        Console.WriteLine(CliUsage.UsageText);
        return 0;
    }

    var verb = args[0];

    // Arg validation for phase verbs happens BEFORE config load so a missing id
    // surfaces a usage error (exit 2) rather than a config-not-found error.
    if (verb == "plan" || verb == "implement" || verb == "review")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
            return 2;
        }
    }

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

    var registry = new TicketCommandRegistry();
    registry.Register("amend", new AmendCommand(ticketing2, eventSink2));

    var wireUpError = WireUpConditionalCommands(verb, registry, secrets2, http2, ticketing2, eventSink2, cwd2);
    if (wireUpError is not null)
    {
        Console.Error.WriteLine($"Secret error: {wireUpError}");
        return 3;
    }

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

    if (verb != "plan" && verb != "implement" && verb != "review")
    {
        Console.Error.WriteLine($"Unknown subcommand: {verb}");
        Console.Error.WriteLine(CliUsage.UsageText);
        return 2;
    }

    var ticketId = args[1];
    var cwd = Directory.GetCurrentDirectory();

    var sessionId = Guid.NewGuid().ToString("N");
    var http = new HttpClient();
    var ticketing = new PlaneTicketingClient(http, new PlaneClientOptions
    {
        BaseUrl = config2.Ticketing.PlaneBaseUrl,
        ApiToken = secrets2.PlaneApiToken,
        WorkspaceSlug = config2.Ticketing.PlaneWorkspaceSlug,
        ProjectId = config2.Ticketing.PlaneProjectId,
        ProjectIdentifier = config2.Ticketing.PlaneProjectIdentifier
    });
    var worker = new ClaudeCodeAgent(new ClaudeCodeOptions
    {
        ExecutablePath = config2.Workers.ClaudeCodeExecutable
    });
    await using var eventSink = new JsonlEventSink(new EventLogOptions
    {
        BaseDirectory = config2.Events.LogDirectory,
        SessionId = sessionId
    });
    var buildOptions = new BuildOptions(
        SessionId: sessionId,
        WorkerName: config2.Workers.DefaultAgent,
        WorkerTimeout: TimeSpan.FromMinutes(config2.Workers.TimeoutMinutes));

    if (verb == "plan")
    {
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
    else if (verb == "implement")
    {
        var phase = new ImplementPhase(ticketing, worker, eventSink, buildOptions);
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

        if (!result.Success)
        {
            Console.Error.WriteLine($"Implement phase failed: {result.FailureReason}");
            return 1;
        }

        Console.WriteLine($"Implement complete: {result.TicketId} commit={result.CommitSha} branch={result.BranchName}");
        return 0;
    }
    else // review
    {
        var verifierWorkerOptions = new WorkerOptions(
            TimeSpan.FromMinutes(config2.Review.VerifierTimeoutMinutes),
            config2.Review.VerifierAllowedTools);
        var reviewOptions = new ReviewOptions(config2.Review.Checks, verifierWorkerOptions);
        var phase = new ReviewPhase(ticketing, worker, eventSink, buildOptions, reviewOptions);
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

        if (!result.Success)
        {
            Console.Error.WriteLine($"Review phase failed: {result.FailureReason}");
            return 4;
        }

        Console.WriteLine($"Review complete: {result.TicketId} verdict={result.Verdict}");
        if (!string.IsNullOrEmpty(result.VerdictRationale))
            Console.WriteLine($"  rationale: {result.VerdictRationale}");

        return result.Verdict == ThroughlineBuild.Contracts.Models.VerdictKind.Pass ? 0 : 1;
    }
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
