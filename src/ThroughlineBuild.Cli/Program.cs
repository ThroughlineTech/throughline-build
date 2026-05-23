using System.Net.Http;
using ThroughlineBuild.Cli;
using ThroughlineBuild.EventLog;
using ThroughlineBuild.Phases;
using ThroughlineBuild.Plane;
using ThroughlineBuild.Workers.ClaudeCode;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    const string Usage = """
build - Throughline Build

Usage:
  build plan <ticket-id>    Run the plan phase for a ticket
  build --help              Show this help

Exit codes:
  0  Success
  1  Phase failure
  2  Config error
  3  Missing secret (env var not set)
""";

    if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    if (args[0] != "plan")
    {
        Console.Error.WriteLine($"Unknown subcommand: {args[0]}");
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
