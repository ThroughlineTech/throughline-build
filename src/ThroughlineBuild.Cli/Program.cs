using System.Net.Http;
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
    if (verb == "plan" || verb == "implement" || verb == "review" || verb == "ship")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Error: ticket-id is required");
            Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
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

    var configDir = Path.GetDirectoryName(configPath2) ?? cwd2;
    string ResolveLogDir(string raw) =>
        Path.IsPathRooted(raw) ? raw : Path.Combine(configDir, raw);

    if (verb == "amend" || verb == "close" || verb == "defer" || verb == "reopen")
    {
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
        await using var jsonlEventSink2 = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
            SessionId = sessionId2
        });
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

    if (verb != "plan" && verb != "implement" && verb != "review" && verb != "ship")
    {
        Console.Error.WriteLine($"Unknown subcommand: {verb}");
        Console.Error.WriteLine(CliUsage.UsageText);
        return 2;
    }

    var ticketId = args[1];
    var cwd = resolvedCwd;

    var sessionId = Guid.NewGuid().ToString("N");

    // --debug: capture worker stdin/stdout/stderr/envelope into .build/sessions/<session-id>/
    // The same session-id is shared with the JSONL event sink so the two sinks correlate.
    // Create the dir eagerly so the "Debug capture:" line at exit always points somewhere
    // real, even when the phase fails before the worker spawns (e.g. early git errors).
    string? debugCaptureDir = debugMode
        ? Path.GetFullPath(Path.Combine(cwd, ".build", "sessions", sessionId))
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
    var worker = new ClaudeCodeAgent(new ClaudeCodeOptions
    {
        ExecutablePath = config2.Workers.ClaudeCodeExecutable,
        MaxOutputTokens = config2.Workers.MaxOutputTokens
    });
    await using var jsonlEventSink = new JsonlEventSink(new EventLogOptions
    {
        BaseDirectory = ResolveLogDir(config2.Events.LogDirectory),
        SessionId = sessionId
    });
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
        ? $".build/sessions/{sessionId}/"
        : $".build/events/{sessionId}.jsonl";

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
        var phase = new PlanPhase(ticketing, worker, eventSink, buildOptions, project: config2.Project);
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
                Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");
            return 1;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");
        return 0;
    }
    else if (verb == "implement")
    {
        var phase = new ImplementPhase(ticketing, worker, eventSink, buildOptions, project: config2.Project);
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
                Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");
            return 1;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");
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
        var phase = new ReviewPhase(ticketing, worker, eventSink, buildOptions, reviewOptions, project: config2.Project);
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
                Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");
            return 4;
        }

        if (debugCaptureDir is not null)
            Console.WriteLine($"Debug capture: .build/sessions/{sessionId}/");

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
