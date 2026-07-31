using System.Net.Http;
using ThroughlineBuild.Cli.Json;
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
using ThroughlineBuild.Workers.Common;
using ThroughlineBuild.Workers.Copilot;
using ThroughlineBuild.Workers.Gemini;

namespace ThroughlineBuild.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, WorkerAgentBuilder.Create);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, AgentConfig, IWorkerAgent> workerAgentBuilder)
    {
        if (ClaudeStopHookCommand.IsMatch(args))
            return await ClaudeStopHookCommand.RunAsync(args);

        var helpRegistry = HelpRegistryFactory.Build();
        var helpTopicRegistry = HelpTopicRegistry.Build();

        if (args.Length == 1 && (args[0] == "-V" || args[0] == "--version"))
        {
            Console.WriteLine(BuildVersion.Current);
            return 0;
        }

        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            Console.Write(Tier0Renderer.Render(helpRegistry));
            return 0;
        }

        if (args[0] == "help")
        {
            if (args.Length == 1)
            {
                Console.Write(Tier0Renderer.Render(helpRegistry));
                return 0;
            }

            if (args.Length == 2 && helpTopicRegistry.TryGet(args[1]) is { } topic)
            {
                Console.Write(HelpTopicRenderer.Render(topic));
                return 0;
            }

            var topicName = args.Length >= 2 ? args[1] : string.Empty;
            Console.Error.Write(HelpTopicRenderer.RenderUnknownTopic(topicName, helpTopicRegistry.TopicNames));
            return 2;
        }

        var boolFlags = CliArgParser.ExtractBoolFlags(args);
        bool debugMode = boolFlags.Debug;
        bool quietMode = boolFlags.Quiet;
        bool summaryJson = boolFlags.SummaryJson;
        bool jsonOutput = boolFlags.Json;
        bool errorLocation = boolFlags.ErrorLocation;
        bool noAutoResolve = boolFlags.NoAutoResolve;
        bool noAutoMerge = boolFlags.NoAutoMerge;
        bool noPush = boolFlags.NoPush;
        bool continuePastFailure = boolFlags.ContinuePastFailure;
        bool fromBrief = boolFlags.FromBrief;
        bool skipBaseline = boolFlags.SkipBaseline;
        bool chainDryRun = false;
        string? chainMaxDepth = null;
        var filteredArgs = boolFlags.Remaining;
        args = filteredArgs.ToArray();

        // Second pre-pass: extract --agent / --agent-<phase> flag pairs before verb dispatch.
        var (agentAll, agentPlanFlag, agentImplementFlag, agentReviewFlag, argsAfterAgentFlags) =
            CliArgParser.ExtractAgentFlags(args);
        args = ((List<string>)argsAfterAgentFlags).ToArray();

        var verb = args[0];
        var cliVerbRegistry = CliVerbRegistryFactory.Build();
        cliVerbRegistry.TryGet(verb, out var registeredVerb);
        var verbKind = registeredVerb?.Kind;
        IReadOnlyList<string>? batchImplementTicketIds = null;
        bool batchImplementAllChildren = false;
        if (verbKind == CliVerbKind.Chain)
        {
            var chainFlags = CliArgParser.ExtractChainTraversalFlags(args);
            if (chainFlags.Error is not null)
            {
                Console.Error.WriteLine(chainFlags.Error);
                return 2;
            }

            chainDryRun = chainFlags.DryRun;
            chainMaxDepth = chainFlags.MaxDepth;
            args = chainFlags.Remaining.ToArray();

            var (extractedBatchTicketIds, isAllChildren, batchImplementError, argsAfterBatchImplementFlag) =
                CliArgParser.ExtractBatchImplementFlag(args);
            if (batchImplementError is not null)
            {
                Console.Error.WriteLine(batchImplementError);
                Console.Error.WriteLine("Usage: build chain <ticket-id> [--batch-implement [TLB-1,TLB-2,...]]");
                return 2;
            }

            batchImplementTicketIds = extractedBatchTicketIds;
            batchImplementAllChildren = isAllChildren;
            args = ((List<string>)argsAfterBatchImplementFlag).ToArray();
        }

        if (verbKind == CliVerbKind.OpDoc && args.Length >= 2 && args[1] == "new")
        {
            bool hasHelpFlag = args.Skip(2).Any(a => a == "-h" || a == "--help");
            if (hasHelpFlag)
            {
                Console.WriteLine("Usage: build op-doc new <slug> [--write]");
                Console.WriteLine();
                Console.WriteLine("Generate a minimal valid op-doc skeleton. Without --write, writes to stdout.");
                Console.WriteLine("--write  Write to docs/op-docs/op-<slug>.md instead of stdout.");
                return 0;
            }
        }

        // Per-command help: build <verb> [any-position] -h|--help
        // Short-circuits before argument validation so "build ship --help" works without a ticket ID.
        {
            bool hasHelpFlag = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "-h" || args[i] == "--help")
                {
                    hasHelpFlag = true;
                    break;
                }
            }
            if (hasHelpFlag)
            {
                var cmdHelp = helpRegistry.TryGet(verb);
                Console.Write(cmdHelp != null
                    ? Tier1Renderer.Render(cmdHelp)
                    : Tier0Renderer.Render(helpRegistry));
                return 0;
            }
        }

        // Extract ticket IDs for verbs that need them.
        IReadOnlyList<string> ticketIds = Array.Empty<string>();
        if (verbKind is CliVerbKind.Plan or CliVerbKind.Implement or CliVerbKind.Review
            or CliVerbKind.Ship or CliVerbKind.Chain or CliVerbKind.Rework or CliVerbKind.Decompose)
        {
            var (extracted, _) = CliArgParser.ExtractTicketIds(args);
            ticketIds = extracted;
        }

        // Arg validation for phase verbs happens BEFORE config load so a missing id
        // surfaces a usage error (exit 2) rather than a config-not-found error.
        if (verbKind is CliVerbKind.Plan or CliVerbKind.Implement or CliVerbKind.Review
            or CliVerbKind.Ship or CliVerbKind.Chain or CliVerbKind.Rework or CliVerbKind.Decompose)
        {
            if (ticketIds.Count == 0 || string.IsNullOrWhiteSpace(ticketIds[0]))
            {
                Console.Error.WriteLine("Error: ticket-id is required");
                Console.Error.WriteLine($"Usage: build {verb} <ticket-id> [ticket-id ...]");
                return 2;
            }

            // For rework and decompose: reject multiple positional ticket IDs (single ticket only).
            if (verbKind is CliVerbKind.Rework or CliVerbKind.Decompose && ticketIds.Count > 1)
            {
                Console.Error.WriteLine($"Error: build {verb} accepts exactly one ticket ID; multi-ticket dispatch is not supported.");
                return 2;
            }
        }

        // Early arg validation for scaffold verb: op-doc-path is a required positional.
        if (verbKind == CliVerbKind.Scaffold)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            {
                Console.Error.WriteLine("Error: op-doc-path is required");
                Console.Error.WriteLine("Usage: build scaffold <op-doc-path> [--validate-only] [--dry-run] [--accept-warnings] [--no-profile] [--force-profile] [--debug]");
                return 2;
            }
        }

        // Early validation for new verb using the argument classifier.
        if (verbKind == CliVerbKind.New)
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

        if (registeredVerb?.RunsBeforeConfig == true)
        {
            // 'build init' must run before config load - it bootstraps the config file.
            if (verbKind == CliVerbKind.Init)
            {
                // Reject misspelled/unknown flags up front so a typo (e.g. --workplace for
                // --workspace) fails loudly instead of being silently dropped and falling through
                // to a prompt for a raw project id.
                var initBoolFlags = new HashSet<string>(StringComparer.Ordinal) { "--force", "--print-template", "--no-interactive" };
                var initValueFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "--plane-url", "--workspace", "--project-id", "--project-name",
            "--token", "--token-env", "--from",
        };
                var unknownInitFlag = CliArgParser.FindUnknownFlag(filteredArgs, initBoolFlags, initValueFlags);
                if (unknownInitFlag != null)
                {
                    Console.Error.WriteLine($"Error: unknown flag for 'build init': {unknownInitFlag}");
                    Console.Error.WriteLine(
                        "Recognized: --force --print-template --no-interactive --plane-url --workspace --project-id --project-name --token --token-env --from");
                    Console.Error.WriteLine("See 'build --help' for details.");
                    return 2;
                }

                var force = filteredArgs.Contains("--force");
                var printTemplate = filteredArgs.Contains("--print-template");
                var noInteractive = filteredArgs.Contains("--no-interactive");
                var initPlaneUrl = CliArgParser.GetFlagValue(filteredArgs, "--plane-url");
                var initWorkspace = CliArgParser.GetFlagValue(filteredArgs, "--workspace");
                var initProjectId = CliArgParser.GetFlagValue(filteredArgs, "--project-id");
                var initProjectName = CliArgParser.GetFlagValue(filteredArgs, "--project-name");
                var initToken = CliArgParser.GetFlagValue(filteredArgs, "--token");
                var initTokenEnv = CliArgParser.GetFlagValue(filteredArgs, "--token-env");
                var initFromFile = CliArgParser.GetFlagValue(filteredArgs, "--from");
                using var initCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; initCts.Cancel(); };
                try
                {
                    return await InitCommand.ExecuteAsync(rawCwd, force, printTemplate, SystemConsole.Instance,
                        planeUrl: initPlaneUrl,
                        workspace: initWorkspace,
                        projectId: initProjectId,
                        projectName: initProjectName,
                        token: initToken,
                        tokenEnv: initTokenEnv,
                        fromFile: initFromFile,
                        // --print-template returns before this delegate is invoked, so init --print-template
                        // stays offline-safe. Blocking on the async probe is fine in this top-level CLI path.
                        probeCodex: () => new CodexModelProbe().ProbeAsync().GetAwaiter().GetResult(),
                        noInteractive: noInteractive,
                        ct: initCts.Token);
                }
                catch (InitAbortedException)
                {
                    // Operator typed 'q'/'quit' at a prompt. Distinct from Ctrl-C: clean bail-out, exit 5.
                    // (This catch must precede the OperationCanceledException one - it is more derived.)
                    Console.Error.WriteLine("Aborted.");
                    return 5;
                }
                catch (OperationCanceledException)
                {
                    // Ctrl-C: CancelKeyPress cancelled initCts, which unblocked the prompt read.
                    Console.Error.WriteLine("Cancelled.");
                    return 1;
                }
                catch (PlaneApiException ex)
                {
                    Console.Error.WriteLine($"Command 'init' failed: Plane API {ex.Status}: {ex.Body}");
                    return 1;
                }
            }

            // 'build settarget' manages config without requiring Plane or worker setup;
            // dispatch early like 'build init'.
            if (verbKind == CliVerbKind.SetTarget)
            {
                var unset = filteredArgs.Contains("--unset");
                string? stBranch = (filteredArgs.Count > 1 && !filteredArgs[1].StartsWith("--"))
                    ? filteredArgs[1]
                    : null;
                return SetTargetCommand.Execute(rawCwd, stBranch, unset, SystemConsole.Instance);
            }

            // 'build user-guide' writes the embedded operator guide; runs without config.
            if (verbKind == CliVerbKind.UserGuide)
            {
                var force = filteredArgs.Contains("--force");
                var printTemplate = filteredArgs.Contains("--print-template");
                return UserGuideCommand.Execute(rawCwd, force, printTemplate, SystemConsole.Instance);
            }

            // 'build op-doc <spec|new>' runs without config: 'spec' prints or writes the embedded
            // authoring spec; 'new' emits a minimal valid op-doc skeleton for the given slug.
            if (verbKind == CliVerbKind.OpDoc)
            {
                var opDocSub = args.Length >= 2 ? args[1] : null;

                if (opDocSub == "spec")
                {
                    var recognized = new HashSet<string>(StringComparer.Ordinal)
            {
                "op-doc",
                "spec",
                "--print",
                "--write",
                "--force",
            };
                    foreach (var arg in args)
                    {
                        if (!recognized.Contains(arg))
                        {
                            Console.Error.WriteLine($"Error: unknown argument: {arg}");
                            Console.Error.WriteLine("Usage: build op-doc spec [--print] [--write] [--force]");
                            return 2;
                        }
                    }

                    var write = filteredArgs.Contains("--write");
                    var force = filteredArgs.Contains("--force");
                    return OpDocSpecCommand.Execute(rawCwd, write, force, SystemConsole.Instance);
                }

                if (opDocSub == "new")
                {
                    if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("Error: slug is required");
                        Console.Error.WriteLine("Usage: build op-doc new <slug> [--write]");
                        return 2;
                    }

                    var opDocSlug = args[2];
                    if (!OpDocSkeletonGenerator.IsValidSlug(opDocSlug))
                    {
                        Console.Error.WriteLine("Error: slug must be kebab-case: lowercase letters and digits separated by single hyphens, starting with a letter.");
                        return 2;
                    }

                    bool write = false;
                    for (int i = 3; i < args.Length; i++)
                    {
                        if (args[i] == "--write")
                        {
                            write = true;
                        }
                        else
                        {
                            Console.Error.WriteLine($"Error: unknown option for build op-doc new: {args[i]}");
                            Console.Error.WriteLine("Usage: build op-doc new <slug> [--write]");
                            return 2;
                        }
                    }

                    var skeleton = OpDocSkeletonGenerator.Render(opDocSlug);
                    if (!write)
                    {
                        Console.Write(skeleton);
                        return 0;
                    }

                    var opDocDirectory = Path.Combine(rawCwd, "docs", "op-docs");
                    var opDocPath = Path.Combine(opDocDirectory, $"op-{opDocSlug}.md");
                    if (File.Exists(opDocPath))
                    {
                        Console.Error.WriteLine($"Error: op-doc already exists: {Path.GetRelativePath(rawCwd, opDocPath)}");
                        return 2;
                    }

                    Directory.CreateDirectory(opDocDirectory);
                    File.WriteAllText(opDocPath, skeleton);
                    Console.WriteLine(Path.GetRelativePath(rawCwd, opDocPath));
                    return 0;
                }

                Console.Error.WriteLine("Error: op-doc subcommand is required");
                Console.Error.WriteLine("Usage: build op-doc spec [--print] [--write] [--force]");
                Console.Error.WriteLine("       build op-doc new <slug> [--write]");
                return 2;
            }

            // 'build models refresh' re-probes Codex and rewrites the [workers.codex.sizes] block
            // in place. Runs in the early pre-config-load band like 'init' because it edits the
            // config file rather than consuming it.
            if (verbKind == CliVerbKind.Models)
            {
                var modelsSub = args.Length >= 2 ? args[1] : null;
                if (modelsSub != "refresh")
                {
                    Console.Error.WriteLine("Error: models subcommand is required");
                    Console.Error.WriteLine("Usage: build models refresh");
                    return 2;
                }
                if (args.Length > 2)
                {
                    Console.Error.WriteLine($"Error: unknown argument: {args[2]}");
                    Console.Error.WriteLine("Usage: build models refresh");
                    return 2;
                }
                return ModelsRefreshCommand.Execute(rawCwd, SystemConsole.Instance,
                    () => new CodexModelProbe().ProbeAsync().GetAwaiter().GetResult());
            }

            throw new InvalidOperationException($"Pre-config verb '{registeredVerb.Name}' has no handler.");
        }

        var bootstrap = await CliBootstrap.CreateAsync(rawCwd, CancellationToken.None);
        if (bootstrap.Failure is { } bootstrapFailure)
        {
            if (jsonOutput)
                CliEnvelopeWriter.WriteError(Console.Out, bootstrapFailure.JsonErrorCode, bootstrapFailure.Message);
            else
            {
                Console.Error.WriteLine($"{bootstrapFailure.HumanPrefix}: {bootstrapFailure.Message}");
                if (errorLocation) Console.Error.WriteLine(FirstExceptionFrame(bootstrapFailure.Cause));
            }
            return bootstrapFailure.ExitCode;
        }

        using var cliContext = bootstrap.Context!;
        if (registeredVerb is null)
        {
            Console.Error.WriteLine($"Unknown subcommand: {verb}");
            Console.Error.WriteLine("See 'build --help'.");
            return 2;
        }

        var resolvedCwd = cliContext.WorkingDirectory;
        var configuredCwd = cliContext.WorkingDirectory;
        var config = cliContext.Config;
        var secrets = cliContext.Secrets;
        string ResolveLogDir(string raw) => cliContext.ResolveLogDirectory(raw);
        var sessionContext = cliContext.SessionContext;

        // Standalone deterministic worktree lifecycle for caller-owned conductor loops.
        // This path composes only git, filesystem, and the configured install command; it
        // never constructs or invokes a worker agent.
        if (verbKind == CliVerbKind.Worktree)
        {
            var worktreeRoot = Path.IsPathRooted(config.Worktree.Root)
                ? Path.GetFullPath(config.Worktree.Root)
                : Path.GetFullPath(Path.Combine(configuredCwd, config.Worktree.Root));
            var worktreeManager = new WorktreeLeaseManager(
                new ProcessGitClient(configuredCwd),
                new ProcessInstallCommandRunner(),
                new WorktreeLeaseOptions(
                    RepositoryPath: configuredCwd,
                    MainWorktreePath: configuredCwd,
                    WorktreeRoot: worktreeRoot,
                    SeedAllowlist: config.Worktree.SeedFiles,
                    InstallCommand: config.Project.InstallCommand));
            using var worktreeCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; worktreeCts.Cancel(); };
            try
            {
                return await WorktreeCommand.ExecuteAsync(
                    args, jsonOutput, worktreeManager, Console.Out, Console.Error, worktreeCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (jsonOutput)
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Failure, "cancelled");
                else
                    Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // Standalone configured gate for caller-owned conductor loops. Use the raw cwd
        // so invocation from a leased worktree checks that tree, not the primary tree
        // resolved by bootstrap. This path never constructs a worker agent.
        if (verbKind == CliVerbKind.Gate)
        {
            using var gateCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; gateCts.Cancel(); };
            try
            {
                return await GateCommand.ExecuteAsync(
                    args,
                    jsonOutput,
                    config.Review.Checks,
                    rawCwd,
                    new AutomatedChecksRunner(),
                    Console.Out,
                    Console.Error,
                    gateCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (jsonOutput)
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Failure, "cancelled");
                else
                    Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // Standalone dependency-safe wave planner for caller-owned conductor loops.
        // It reads only JSON input and config, and never constructs a worker or ticket client.
        if (verbKind == CliVerbKind.Waves)
        {
            using var wavesCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; wavesCts.Cancel(); };
            try
            {
                return await WavesCommand.ExecuteAsync(
                    args,
                    jsonOutput,
                    config.Waves,
                    Console.In,
                    Console.Out,
                    Console.Error,
                    wavesCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (jsonOutput)
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Failure, "cancelled");
                else
                    Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // 'build sweep' removes leftover chain worktrees and merged branches that a prior
        // 'build chain' left behind - the recovery path when a chain was interrupted or
        // preserved-on-failure and so never reached its own end-of-chain sweep. Stack-agnostic:
        // pure git + filesystem, no worker, no Plane. Branch deletion is merged-gated against the
        // target (or --target), so it never discards unshipped commits; --force additionally removes
        // worktrees whose branch is not merged (the branch itself is still kept).
        if (verbKind == CliVerbKind.Sweep)
        {
            var sweepTarget = CliArgParser.GetFlagValue(args, "--target") ?? config.ResolveTargetBranch();
            var sweepForce = Array.IndexOf(args, "--force") >= 0;
            var sweepGit = new ProcessGitClient(configuredCwd);
            var sweeper = new ChainWorktreeSweeper(sweepGit);
            try
            {
                using var sweepCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; sweepCts.Cancel(); };
                var sweepResult = await sweeper.SweepAsync(configuredCwd, sweepTarget, sweepForce, sweepCts.Token);

                Console.WriteLine($"Swept chain artifacts (merge target: {sweepTarget}):");
                Console.WriteLine($"  worktrees removed: {sweepResult.WorktreesRemoved.Count}");
                Console.WriteLine($"  branches deleted:  {sweepResult.BranchesDeleted.Count}");
                foreach (var b in sweepResult.BranchesDeleted)
                    Console.WriteLine($"    - {b}");
                if (sweepResult.BranchesKeptUnmerged.Count > 0)
                {
                    Console.WriteLine("  kept (not merged into target - left intact):");
                    foreach (var b in sweepResult.BranchesKeptUnmerged)
                        Console.WriteLine($"    - {b}");
                }
                if (sweepResult.WorktreesHalted.Count > 0)
                {
                    Console.Error.WriteLine("  worktrees that could not be removed:");
                    foreach (var h in sweepResult.WorktreesHalted)
                        Console.Error.WriteLine($"    - {h}");
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

        if (verbKind == CliVerbKind.List)
        {
            var sharedTicketing = cliContext.Ticketing;

            var cmd = new ListCommand(sharedTicketing, Console.Out);
            var extraArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i + 1 < args.Length; i += 2)
            {
                var key = args[i];
                if (key.StartsWith("--"))
                    key = key.Substring(2);
                extraArgs[key] = args[i + 1];
            }

            // --json: emit the result as a versioned envelope instead of the human table.
            if (jsonOutput)
            {
                TicketState? listState = null;
                if (extraArgs.TryGetValue("state", out var stateArg) && !string.IsNullOrWhiteSpace(stateArg))
                {
                    if (!Enum.TryParse<TicketState>(stateArg, ignoreCase: true, out var parsedState))
                    {
                        CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, $"invalid --state value: {stateArg}");
                        return 2;
                    }
                    listState = parsedState;
                }
                extraArgs.TryGetValue("parent", out var listParent);
                extraArgs.TryGetValue("type", out var listType);

                using var jsonListCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; jsonListCts.Cancel(); };
                try
                {
                    var listed = await sharedTicketing.QueryAsync(new TicketQuery(listState, listParent, listType), jsonListCts.Token);
                    CliEnvelopeWriter.WriteList(Console.Out, listed);
                    return 0;
                }
                catch (PlaneApiException ex)
                {
                    return PlaneCliError.Report("list", ex, jsonOutput);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return 1;
                }
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

        // 'build get <ticket-id> [--json]' reads a single ticket. The first read verb on the
        // versioned --json envelope (TLB-541): the agent shells out and parses the envelope
        // instead of curling Plane itself. No worker, no event log - a thin Plane read.
        if (verbKind == CliVerbKind.Get)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket-id is required");
                else
                {
                    Console.Error.WriteLine("Error: ticket-id is required");
                    Console.Error.WriteLine("Usage: build get <ticket-id> [--json]");
                }
                return 2;
            }

            var getTicketId = args[1];
            var sharedTicketing = cliContext.Ticketing;

            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                var ticket = await sharedTicketing.GetAsync(getTicketId, verbCts.Token);

                if (jsonOutput)
                {
                    CliEnvelopeWriter.WriteTicket(Console.Out, CliEnvelopeWriter.ToView(ticket));
                }
                else
                {
                    Console.WriteLine($"{ticket.Id}  {ticket.Title}");
                    Console.WriteLine($"  state:  {ticket.State}");
                    Console.WriteLine($"  type:   {(string.IsNullOrEmpty(ticket.Type) ? "-" : ticket.Type)}");
                    Console.WriteLine($"  size:   {ticket.Size}    risk: {ticket.Risk}");
                    if (ticket.ParentId is not null) Console.WriteLine($"  parent: {ticket.ParentId}");
                    if (ticket.Labels.Count > 0) Console.WriteLine($"  labels: {string.Join(", ", ticket.Labels)}");
                    foreach (var rel in ticket.Relations) Console.WriteLine($"  rel:    {rel.Kind} -> {rel.TargetId}");

                    // Render the body too, reusing the same HTML->text renderer the --json
                    // path uses (CliEnvelopeWriter.ToView), so text and JSON stay in sync.
                    var bodyText = HtmlToText.Render(ticket.DescriptionHtml);
                    if (!string.IsNullOrWhiteSpace(bodyText))
                    {
                        Console.WriteLine();
                        Console.WriteLine(bodyText);
                    }
                }
                return 0;
            }
            catch (ArgumentException ex)
            {
                // ParseSequenceId rejects a malformed id ("abc", "TLB-") - a usage error, not a miss.
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                else Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (KeyNotFoundException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                else Console.Error.WriteLine($"Command 'get' failed: {ex.Message}");
                return 1;
            }
            catch (PlaneApiException ex)
            {
                // A 404 here (e.g. wrong/unset project) arrives with the actionable message already set
                // by PlaneTicketingClient; PlaneCliError preserves it instead of re-deriving from ex.Body.
                return PlaneCliError.Report("get", ex, jsonOutput);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // 'build comments <ticket-id> [--json]' lists a ticket's comments (read).
        if (verbKind == CliVerbKind.Comments)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket-id is required");
                else { Console.Error.WriteLine("Error: ticket-id is required"); Console.Error.WriteLine("Usage: build comments <ticket-id> [--json]"); }
                return 2;
            }
            var commentsId = args[1];
            var sharedTicketing = cliContext.Ticketing;
            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                var comments = await sharedTicketing.GetCommentsAsync(commentsId, verbCts.Token);
                if (jsonOutput)
                {
                    CliEnvelopeWriter.WriteComments(Console.Out, comments);
                }
                else if (comments.Count == 0)
                {
                    Console.WriteLine("no comments");
                }
                else
                {
                    foreach (var c in comments)
                    {
                        Console.WriteLine($"[{c.CreatedAt:u}] {c.Id}");
                        Console.WriteLine(HtmlToText.Render(c.Body));
                        Console.WriteLine();
                    }
                }
                return 0;
            }
            catch (ArgumentException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                else Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (KeyNotFoundException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                else Console.Error.WriteLine($"Command 'comments' failed: {ex.Message}");
                return 1;
            }
            catch (PlaneApiException ex)
            {
                return PlaneCliError.Report("comments", ex, jsonOutput);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // 'build comment <ticket-id> <body|-> [--json]' posts a comment (write). The body is markdown
        // (or "-" to read from stdin) and is rendered to Plane HTML. This is the agent's write-back path.
        if (verbKind == CliVerbKind.Comment)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket-id is required");
                else { Console.Error.WriteLine("Error: ticket-id is required"); Console.Error.WriteLine("Usage: build comment <ticket-id> <body|-> [--json]"); }
                return 2;
            }
            var commentTicketId = args[1];
            string commentBody;
            if (args.Length >= 3 && args[2] == "-")
                commentBody = Console.In.ReadToEnd();
            else if (args.Length >= 3 && !args[2].StartsWith("--"))
                commentBody = args[2];
            else
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "comment body is required (pass text or '-' to read stdin)");
                else { Console.Error.WriteLine("Error: comment body is required"); Console.Error.WriteLine("Usage: build comment <ticket-id> <body|-> [--json]"); }
                return 2;
            }
            if (string.IsNullOrWhiteSpace(commentBody))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "comment body is empty");
                else Console.Error.WriteLine("Error: comment body is empty");
                return 2;
            }
            var commentHtml = MarkdownHtml.Render(commentBody);
            var sharedTicketing = cliContext.Ticketing;
            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                var newCommentId = await sharedTicketing.CreateCommentAsync(commentTicketId, commentHtml, verbCts.Token);
                if (jsonOutput) CliEnvelopeWriter.WriteCommentCreated(Console.Out, newCommentId);
                else Console.WriteLine($"Commented on {commentTicketId} (comment {newCommentId})");
                return 0;
            }
            catch (ArgumentException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                else Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (KeyNotFoundException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                else Console.Error.WriteLine($"Command 'comment' failed: {ex.Message}");
                return 1;
            }
            catch (PlaneApiException ex)
            {
                return PlaneCliError.Report("comment", ex, jsonOutput);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // 'build transition <ticket-id> <state> [--json]' moves a ticket to a new state (write).
        // State is matched case/space/hyphen-insensitively to the TicketState names.
        if (verbKind == CliVerbKind.Transition)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--"))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket-id is required");
                else { Console.Error.WriteLine("Error: ticket-id is required"); Console.Error.WriteLine($"Usage: build transition <ticket-id> <state> [--json]   (states: {string.Join(", ", Enum.GetNames<TicketState>())})"); }
                return 2;
            }
            if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("--"))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "state is required");
                else { Console.Error.WriteLine("Error: state is required"); Console.Error.WriteLine($"Usage: build transition <ticket-id> <state> [--json]   (states: {string.Join(", ", Enum.GetNames<TicketState>())})"); }
                return 2;
            }
            var transitionId = args[1];
            var stateRaw = args[2];
            var stateNormalized = stateRaw.Replace(" ", "").Replace("-", "").Replace("_", "");
            if (!Enum.TryParse<TicketState>(stateNormalized, ignoreCase: true, out var targetState))
            {
                var validStates = string.Join(", ", Enum.GetNames<TicketState>());
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, $"invalid state '{stateRaw}'; valid states: {validStates}");
                else Console.Error.WriteLine($"Error: invalid state '{stateRaw}'; valid states: {validStates}");
                return 2;
            }
            var sharedTicketing = cliContext.Ticketing;
            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                await sharedTicketing.TransitionAsync(transitionId, targetState, verbCts.Token);
                if (jsonOutput) CliEnvelopeWriter.WriteTransition(Console.Out, transitionId, targetState);
                else Console.WriteLine($"{transitionId} -> {targetState}");
                return 0;
            }
            catch (ArgumentException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                else Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (KeyNotFoundException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                else Console.Error.WriteLine($"Command 'transition' failed: {ex.Message}");
                return 1;
            }
            catch (PlaneApiException ex)
            {
                return PlaneCliError.Report("transition", ex, jsonOutput);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // Explicit relation management. GetRelationsAsync intentionally treats a 404 as an empty
        // optional chain dependency lookup; this verb uses the strict management methods instead.
        if (verbKind == CliVerbKind.Relate)
        {
            var sharedTicketing = cliContext.Ticketing;
            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                return await RelateCommand.ExecuteAsync(
                    args, jsonOutput, sharedTicketing, Console.Out, Console.Error, verbCts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        // 'build setup' provisions the Plane project (states + labels) to meet workflow criteria.
        // No worker, no event log - a thin read/diff/create against the Plane API.
        if (verbKind == CliVerbKind.Setup)
        {
            var checkOnly = filteredArgs.Contains("--check");
            var sharedTicketing = cliContext.Ticketing;
            var setupCmd = new SetupCommand(sharedTicketing, new FileSystemLocalRepoOps(configuredCwd));
            try
            {
                using var verbCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };
                var setupExit = await setupCmd.ExecuteAsync(checkOnly, SystemConsole.Instance, verbCts.Token);
                // Diagnose the configured Claude transport (executable, version, platform) so an operator
                // learns about an unsupported interactive-hook setup here, before running a phase.
                var transportExit = await ClaudeTransportPreflight.ReportAsync(
                    config.Workers, Console.Out, Console.Error, verbCts.Token);
                return setupExit != 0 ? setupExit : transportExit;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
            catch (PlaneApiException ex) when (ex.Status == 404)
            {
                // A 404 on a project-scoped route means the configured project id does not
                // resolve - surface the actionable "project not found" remedy, not the raw body.
                Console.Error.WriteLine("Command 'setup' failed: " + PlaneTicketingClient.BuildProjectNotFoundMessage(
                    config.Ticketing.PlaneWorkspaceSlug, config.Ticketing.PlaneProjectId, ex));
                return 1;
            }
            catch (PlaneApiException ex)
            {
                Console.Error.WriteLine($"Command 'setup' failed: Plane API {ex.Status}: {ex.Body}");
                return 1;
            }
        }

        if (verbKind is CliVerbKind.Amend or CliVerbKind.Close or CliVerbKind.Defer or CliVerbKind.Reopen)
        {
            var commandSessionId = Guid.NewGuid().ToString("N");
            var verbTicketIdForName = args.Length >= 2 ? args[1] : null;
            var commandFileStem = SessionFileNameBuilder.Build(
                projectName: config.Ticketing.PlaneProjectName,
                projectIdentifier: config.Ticketing.PlaneProjectIdentifier,
                verb: verb,
                ticketId: verbTicketIdForName,
                extraSlug: null,
                timestamp: DateTimeOffset.Now);
            var sharedHttpClient = cliContext.HttpClient;
            var sharedTicketing = cliContext.Ticketing;
            await using var commandJsonlEventSink = new JsonlEventSink(new EventLogOptions
            {
                BaseDirectory = ResolveLogDir(config.Events.LogDirectory),
                SessionId = commandSessionId,
                FileNameStem = commandFileStem
            }, sessionContext);
            var commandEventSink = new RecordingEventSink(commandJsonlEventSink);

            var registry = new TicketCommandRegistry();
            registry.Register("amend", new AmendCommand(sharedTicketing, commandEventSink));

            var wireUpError = WireUpConditionalCommands(
                registeredVerb.Kind,
                registry,
                secrets,
                config.Llm,
                sharedHttpClient,
                sharedTicketing,
                commandEventSink,
                configuredCwd);
            if (wireUpError is not null)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.MissingSecret, wireUpError);
                else Console.Error.WriteLine($"Secret error: {wireUpError}");
                return 3;
            }
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket-id is required");
                else
                {
                    Console.Error.WriteLine($"Error: ticket-id is required");
                    Console.Error.WriteLine($"Usage: build {verb} <ticket-id>");
                }
                return 2;
            }

            var verbTicketId = args[1];
            var extraArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            int parseStart = 2;
            if (verbKind is CliVerbKind.Close or CliVerbKind.Defer or CliVerbKind.Reopen
                && args.Length >= 3
                && !args[2].StartsWith("--"))
            {
                extraArgs["reason"] = args[2];
                parseStart = 3;
            }
            TicketCommandContext ctx;
            if (verbKind == CliVerbKind.Amend)
            {
                if (!AmendArgumentParser.TryParse(verbTicketId, args, parseStart, out var amendContext, out var parseError))
                {
                    if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, parseError!);
                    else Console.Error.WriteLine($"Error: {parseError}");
                    return 2;
                }
                ctx = amendContext!;
            }
            else
            {
                // Single-pass: bare bool flags consume 1 slot; key=value pairs consume 2.
                for (int i = parseStart; i < args.Length;)
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
                ctx = new TicketCommandContext(verbTicketId, extraArgs);
            }

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
                if (jsonOutput)
                    TicketCommandEnvelopeWriter.Write(Console.Out, verbTicketId, verb, verbResult);
                if (!verbResult.Success)
                {
                    if (!jsonOutput) Console.Error.WriteLine($"Command '{verb}' failed: {verbResult.Message}");
                    return 1;
                }
                if (!jsonOutput && !string.IsNullOrEmpty(verbResult.Message))
                    Console.WriteLine(verbResult.Message);
                return 0;
            }
            catch (ArgumentException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                else Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (KeyNotFoundException ex)
            {
                if (jsonOutput) CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                else Console.Error.WriteLine($"Command '{verb}' failed: {ex.Message}");
                return 1;
            }
            catch (PlaneApiException ex)
            {
                return PlaneCliError.Report(verb, ex, jsonOutput);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        if (verbKind == CliVerbKind.New)
        {
            var commandSessionId = Guid.NewGuid().ToString("N");
            var commandFileStem = SessionFileNameBuilder.Build(
                projectName: config.Ticketing.PlaneProjectName,
                projectIdentifier: config.Ticketing.PlaneProjectIdentifier,
                verb: verb,
                ticketId: null,
                extraSlug: null,
                timestamp: DateTimeOffset.Now);
            var sharedTicketing = cliContext.Ticketing;
            await using var commandJsonlEventSink = new JsonlEventSink(new EventLogOptions
            {
                BaseDirectory = ResolveLogDir(config.Events.LogDirectory),
                SessionId = commandSessionId,
                FileNameStem = commandFileStem
            }, sessionContext);
            var commandEventSink = new RecordingEventSink(commandJsonlEventSink);

            // Classify argument shape to determine file-mode vs draft-mode.
            var classification = NewVerbArgumentClassifier.Classify(args);

            // Build options: file-mode only needs a stub (no worker); draft-mode needs a real worker.
            bool needsWorker = classification.Kind == NewVerbKind.DraftMode
                            || classification.Kind == NewVerbKind.StdinDraftMode;

            string? newDebugCaptureDirectory = debugMode
                ? Path.GetFullPath(Path.Combine(configuredCwd, ".build", "sessions", commandFileStem))
                : null;
            if (newDebugCaptureDirectory is not null)
                Directory.CreateDirectory(newDebugCaptureDirectory);

            BuildOptions newBuildOptions;
            if (needsWorker)
            {
                newBuildOptions = new BuildOptions(
                    SessionId: commandSessionId,
                    WorkerName: config.Workers.DefaultAgent,
                    WorkerTimeout: TimeSpan.FromMinutes(config.Workers.TimeoutMinutes),
                    DebugCaptureDirectory: newDebugCaptureDirectory,
                    LiveStdoutSink: debugMode ? Console.Out : null,
                    LiveStderrSink: debugMode ? Console.Error : null,
                    ProgressDigestSink: (!debugMode && !quietMode
                        && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1"))
                        ? Console.Error : null,
                    BuildVersion: BuildVersion.Current);
            }
            else
            {
                newBuildOptions = new BuildOptions(
                    SessionId: commandSessionId,
                    WorkerName: "",
                    WorkerTimeout: TimeSpan.Zero,
                    DebugCaptureDirectory: newDebugCaptureDirectory,
                    BuildVersion: BuildVersion.Current);
            }

            var newPhase = new NewPhase(sharedTicketing, commandEventSink, newBuildOptions);

            // build new - --json: strict JSON draft on stdin, no LLM drafter. Create deterministically
            // via NewPhase.RunFromStructuredAsync and emit the {id,uuid,labels,parent,relations} envelope.
            // This is the path that replaces /ticket-new (the agent assembles the draft and shells out).
            if (jsonOutput && classification.Kind == NewVerbKind.StdinDraftMode)
            {
                var draftJson = Console.In.ReadToEnd();
                if (!TicketDraftParser.TryParse(draftJson, out var draft, out var parseError))
                {
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, parseError!);
                    return 2;
                }
                if (string.IsNullOrWhiteSpace(draft!.Title))
                {
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, "ticket draft requires a non-empty title");
                    return 2;
                }
                var draftRelations = new List<Relation>();
                foreach (var relation in draft.Relations ?? Array.Empty<TicketDraftRelation>())
                {
                    if (relation is null || string.IsNullOrWhiteSpace(relation.Kind)
                        || string.IsNullOrWhiteSpace(relation.TargetId))
                    {
                        CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage,
                            "each relation requires non-empty 'kind' and 'targetId' fields");
                        return 2;
                    }
                    if (!RelationKinds.TryNormalize(relation.Kind, out var normalizedRelationKind))
                    {
                        CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage,
                            $"invalid relation type '{relation.Kind}'; valid types: {string.Join(", ", RelationKinds.Allowed)}");
                        return 2;
                    }
                    draftRelations.Add(new Relation(normalizedRelationKind, relation.TargetId));
                }

                using var jsonNewCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; jsonNewCts.Cancel(); };
                try
                {
                    var created = await newPhase.RunFromStructuredAsync(
                        draft.Title, draft.Type, draft.Description, draft.AcceptanceCriteria,
                        draft.Labels, draft.Parent, draftRelations, jsonNewCts.Token);
                    CliEnvelopeWriter.WriteNewTicket(Console.Out, new NewTicketView(
                        created.Id,
                        created.Uuid,
                        draft.Labels ?? Array.Empty<string>(),
                        draft.Parent,
                        draftRelations.Select(r => new RelationView(r.Kind, r.TargetId)).ToList()));
                    return 0;
                }
                catch (NewPhaseValidationException ex)
                {
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Usage, ex.Message);
                    return 2;
                }
                catch (KeyNotFoundException ex)
                {
                    // The parent id did not resolve to a ticket.
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.NotFound, ex.Message);
                    return 1;
                }
                catch (PlaneApiException ex)
                {
                    return PlaneCliError.Report("new", ex, jsonOutput);
                }
                catch (RelationEndpointUnavailableException ex)
                {
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.ConfigError, ex.Message);
                    return 2;
                }
                catch (RelationConfigurationException ex)
                {
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.ConfigError, ex.Message);
                    return 2;
                }
                catch (InvalidOperationException ex)
                {
                    // e.g. an unknown label name or issue type rejected by CreateTicketAsync.
                    CliEnvelopeWriter.WriteError(Console.Out, CliErrorCodes.Failure, ex.Message);
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return 1;
                }
            }

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
                var commandContext = new TicketCommandContext("", newCommandArgs);
                var newCommand = new NewCommand(newPhase, config.Ticketing.PlaneBaseUrl, config.Ticketing.PlaneWorkspaceSlug, newBuildOptions.DebugCaptureDirectory);
                try
                {
                    var verbResult = await newCommand.ExecuteAsync(commandContext, verbCts.Token);
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
                var commandContext = new TicketCommandContext("", newCommandArgs);
                var newCommand = new NewCommand(newPhase, config.Ticketing.PlaneBaseUrl, config.Ticketing.PlaneWorkspaceSlug, newBuildOptions.DebugCaptureDirectory);
                try
                {
                    var verbResult = await newCommand.ExecuteAsync(commandContext, verbCts.Token);
                    if (!verbResult.Success)
                    {
                        Console.Error.WriteLine($"Command 'new' failed: {verbResult.Message}");
                        if (newBuildOptions.DebugCaptureDirectory is not null)
                            Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                        return 1;
                    }
                    if (!string.IsNullOrEmpty(verbResult.Message))
                        Console.WriteLine(verbResult.Message);
                    if (newBuildOptions.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                    if (newBuildOptions.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
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
            var draftImplementAgentName = config.Workers.Phases.TryGetValue("implement", out var draftPhaseName)
                ? draftPhaseName
                : config.Workers.DefaultAgent;
            if (!config.Workers.Agents.TryGetValue(draftImplementAgentName, out var draftAgentCfg))
                throw new ConfigException($"missing [workers.{draftImplementAgentName}] sub-table in config");
            var draftWorkerFactory = new WorkerAgentFactory(
                new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal)
                {
                    [draftImplementAgentName] = () => new ClaudeCodeAgent(new ClaudeCodeOptions
                    {
                        ExecutablePath = draftAgentCfg.Executable,
                        MaxOutputTokens = draftAgentCfg.MaxOutputTokens,
                        Sizes = draftAgentCfg.Sizes,
                        Transport = draftAgentCfg.Transport,
                    })
                });
            var draftWorker = draftWorkerFactory.Create(draftImplementAgentName);

            // Fail before the draft worker runs if its interactive-hook transport is unsupported here.
            var draftGateExit = await ClaudeTransportPreflight.GateAsync(
                config.Workers, [draftImplementAgentName], Console.Error, CancellationToken.None);
            if (draftGateExit != 0)
                return draftGateExit;

            var draftPhase = new DraftPhase(draftWorker, newBuildOptions);
            var draftSw = System.Diagnostics.Stopwatch.StartNew();
            DraftResult draftResult;
            try
            {
                draftResult = await draftPhase.RunAsync(
                    new DraftPhaseOptions(draftText, debugMode),
                    configuredCwd,
                    verbCts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                if (newBuildOptions.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                return 1;
            }
            draftSw.Stop();

            if (draftResult.Outcome != ThroughlineBuild.Contracts.Models.DraftOutcome.Ok)
            {
                Console.Error.WriteLine($"draft failed: {draftResult.FailureReason}");
                if (newBuildOptions.DebugCaptureDirectory is not null)
                    Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                return 1;
            }

            Console.WriteLine($"[drafted] from operator text ({draftSw.Elapsed.TotalSeconds:0.0}s)");

            // When --review is set, run interactive review loop before filing.
            string finalBody;
            if (review)
            {
                var loop = new ReviewLoop(
                    SystemConsole.Instance,
                    (text, cancellationToken) => draftPhase.RunAsync(new DraftPhaseOptions(text, debugMode), configuredCwd, cancellationToken),
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

                var reviewedDraftContext = new TicketCommandContext("", newCommandArgs);
                var reviewedDraftCommand = new NewCommand(newPhase, config.Ticketing.PlaneBaseUrl, config.Ticketing.PlaneWorkspaceSlug, newBuildOptions.DebugCaptureDirectory);
                try
                {
                    var verbResult = await reviewedDraftCommand.ExecuteAsync(reviewedDraftContext, verbCts.Token);
                    if (!verbResult.Success)
                    {
                        Console.Error.WriteLine($"Command 'new' failed: {verbResult.Message}");
                        if (newBuildOptions.DebugCaptureDirectory is not null)
                            Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                        return 1;
                    }
                    if (!string.IsNullOrEmpty(verbResult.Message))
                        Console.WriteLine(verbResult.Message);
                    if (newBuildOptions.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled.");
                    if (newBuildOptions.DebugCaptureDirectory is not null)
                        Console.WriteLine($"Debug capture: .build/sessions/{commandFileStem}/");
                    return 1;
                }
            }
            finally
            {
                try { File.Delete(tempBodyPath); } catch { /* best effort cleanup */ }
            }
        }

        if (verbKind == CliVerbKind.Scaffold)
        {
            var scaffoldSessionId = Guid.NewGuid().ToString("N");
            var opDocStem = Path.GetFileNameWithoutExtension(args[1]);
            var scaffoldFileStem = SessionFileNameBuilder.Build(
                projectName: config.Ticketing.PlaneProjectName,
                projectIdentifier: config.Ticketing.PlaneProjectIdentifier,
                verb: verb,
                ticketId: null,
                extraSlug: opDocStem,
                timestamp: DateTimeOffset.Now);
            var scaffoldTicketing = cliContext.Ticketing;
            await using var scaffoldJsonlSink = new JsonlEventSink(new EventLogOptions
            {
                BaseDirectory = ResolveLogDir(config.Events.LogDirectory),
                SessionId = scaffoldSessionId,
                FileNameStem = scaffoldFileStem
            }, sessionContext);
            var scaffoldEventSink = new RecordingEventSink(scaffoldJsonlSink);

            var scaffoldPhase = new ScaffoldPhase(scaffoldTicketing, scaffoldEventSink, scaffoldSessionId);
            var scaffoldCommand = new ScaffoldCommand(scaffoldPhase);

            // Parse scaffold-local flags.
            var scaffoldArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            scaffoldArgs["op_doc_path"] = args[1];
            bool noProfile = false;
            bool forceProfile = false;
            bool validateOnlyFlag = false;
            bool dryRunFlag = false;
            for (int i = 2; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--validate-only") { scaffoldArgs["validate_only"] = "true"; validateOnlyFlag = true; }
                else if (a == "--dry-run") { scaffoldArgs["dry_run"] = "true"; dryRunFlag = true; }
                else if (a == "--accept-warnings") scaffoldArgs["accept_warnings"] = "true";
                else if (a == "--no-profile") noProfile = true;
                else if (a == "--force-profile") forceProfile = true;
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

                int scaffoldExit = tag switch
                {
                    ScaffoldExitCategory.Clean => 0,
                    ScaffoldExitCategory.ValidationError => 2,
                    ScaffoldExitCategory.PartialCreation => 3,
                    ScaffoldExitCategory.BackendUnavailable => 4,
                    _ => scaffoldResult.Success ? 0 : 1
                };

                // Derive the project's review/ship checks from the op-doc and write them into
                // .build/config.toml. Runs only on a real creation run; derivation never changes the
                // scaffold exit code (the ticket tree is this command's contract, not the config).
                bool ticketsCreated = tag == ScaffoldExitCategory.Clean || tag == ScaffoldExitCategory.PartialCreation;
                if (ticketsCreated && !validateOnlyFlag && !dryRunFlag && !noProfile)
                {
                    // Under --debug, capture the derivation worker's raw stdin/stdout/stderr and
                    // structured transcript like any phase worker. Without it, a derivation failure
                    // (e.g. a missing PROJECT_PROFILE block) leaves no diagnosable artifact.
                    string? scaffoldDebugDir = debugMode
                        ? Path.GetFullPath(Path.Combine(resolvedCwd, ".build", "sessions",
                            $"scaffold-profile-{DateTimeOffset.Now:yyyy-MM-dd-HHmmss}"))
                        : null;
                    // Gate the profile-derivation worker (the default agent) before it launches. Derivation
                    // is best-effort and never changes the scaffold exit code, so on an unsupported host we
                    // skip it with a clear note rather than fail the scaffold whose ticket tree already exists.
                    var scaffoldGateExit = await ClaudeTransportPreflight.GateAsync(
                        config.Workers, [config.Workers.DefaultAgent], Console.Error, scaffoldCts.Token);
                    if (scaffoldGateExit != 0)
                    {
                        Console.Error.WriteLine(
                            "[build] Skipping profile derivation: the configured Claude transport is unsupported on this " +
                            "host (see above). The ticket tree was created; derive checks later or set transport = \"print\".");
                    }
                    else
                    {
                        await ScaffoldProfileRunner.RunAsync(
                            args[1], resolvedCwd, config.Workers, forceProfile, scaffoldDebugDir, scaffoldCts.Token);
                    }
                }
                else if (noProfile)
                {
                    Console.WriteLine("[scaffold] --no-profile: skipped review-check derivation");
                }

                return scaffoldExit;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 1;
            }
        }

        if (verbKind is not (CliVerbKind.Plan or CliVerbKind.Implement or CliVerbKind.Review
            or CliVerbKind.Ship or CliVerbKind.Chain or CliVerbKind.Rework or CliVerbKind.Decompose))
        {
            Console.Error.WriteLine($"Unknown subcommand: {verb}");
            Console.Error.WriteLine("See 'build --help'.");
            return 2;
        }

        var cwd = resolvedCwd;

        var ticketing = cliContext.Ticketing;
        if (!config.Workers.Agents.TryGetValue(config.Workers.DefaultAgent, out var agentCfg))
            throw new ConfigException($"missing [workers.{config.Workers.DefaultAgent}] sub-table in config");

        // Collect all agent names referenced by phases (plus default) and register them.
        // Also include any names supplied via CLI flags so they are available to the factory
        // (and so that unknown names surface as a clear ConfigException from factory.Create).
        var allAgentNames = new HashSet<string>(StringComparer.Ordinal) { config.Workers.DefaultAgent };
        foreach (var phaseName in config.Workers.Phases.Values)
            allAgentNames.Add(phaseName);
        foreach (var flagAgentName in new[] { agentAll, agentPlanFlag, agentImplementFlag, agentReviewFlag })
        {
            if (flagAgentName is not null && config.Workers.Agents.ContainsKey(flagAgentName))
                allAgentNames.Add(flagAgentName);
        }

        var factoryEntries = new Dictionary<string, Func<IWorkerAgent>>(StringComparer.Ordinal);
        foreach (var agentName in allAgentNames)
        {
            if (!config.Workers.Agents.TryGetValue(agentName, out var aCfg))
                throw new ConfigException($"missing [workers.{agentName}] sub-table in config");
            var capturedCfg = aCfg;
            var capturedName = agentName;
            factoryEntries[agentName] = () => workerAgentBuilder(capturedName, capturedCfg);
        }
        var workerFactory = new WorkerAgentFactory(factoryEntries);

        // Helper: resolve the agent name for a given phase, falling back to default_agent.
        string AgentFor(string phase) =>
            config.Workers.Phases.TryGetValue(phase, out var n) ? n : config.Workers.DefaultAgent;

        // Helper: apply CLI flag overrides on top of config. Per-phase flag wins over --agent,
        // which wins over config. Phases not listed here fall back to AgentFor(phase).
        string EffectiveAgentFor(string phase) =>
            phase == "plan" ? (agentPlanFlag ?? agentAll ?? AgentFor("plan")) :
            phase == "implement" ? (agentImplementFlag ?? agentAll ?? AgentFor("implement")) :
            phase == "review" ? (agentReviewFlag ?? agentAll ?? AgentFor("review")) :
            AgentFor(phase);

        // Capability check for the worker-spawning phase verbs: when the resolved agent uses the
        // interactive-hook Claude transport, verify this host can support it (claude present, version
        // >= minimum, platform) BEFORE the phase starts, so the failure is clean (no worktree cut, no
        // ticket transition) and never falls back to print. The transport's own entry guard
        // (ClaudeCodeInteractiveTransport.ExecuteAsync) backstops every path, but failing here keeps the
        // before-phase contract. The verb -> gated-phases routing (incl. plan only in investigate mode)
        // lives in ClaudeTransportPreflight.PhasesToGateForVerb so it is unit-tested; build new draft and
        // build scaffold gate at their own dispatch sites above.
        var phasesToGate = ClaudeTransportPreflight.PhasesToGateForVerb(verb, config.Plan.IsPromote, fromBrief);
        if (phasesToGate.Count > 0)
        {
            var transportGateExit = await ClaudeTransportPreflight.GateAsync(
                config.Workers,
                phasesToGate.Select(EffectiveAgentFor),
                Console.Error,
                CancellationToken.None);
            if (transportGateExit != 0)
                return transportGateExit;
        }

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
            // action: 0=continue loop, 1=break loop, 2=return directly from RunAsync
            var (iterCode, iterAction) = await RunTicketVerbBodyAsync(
                verb, registeredVerb.Kind, ticketId, args, cwd, ticketing, workerFactory, config,
                ResolveLogDir(config.Events.LogDirectory), sessionContext,
                debugMode, quietMode, summaryJson, errorLocation, noAutoMerge,
                noAutoResolve, continuePastFailure, fromBrief, noPush, skipBaseline, chainDryRun, chainMaxDepth, EffectiveAgentFor,
                batchImplementTicketIds, batchImplementAllChildren);
            dispatchExitCode = iterCode;
            if (iterAction == 2) return iterCode;
            if (iterAction == 1) break;
        }

        // Multi-ticket verbs dispatch complete; rework and decompose are single-ticket only.
        if (dispatchExitCode != 0)
            return dispatchExitCode;

        // Single-ticket verbs: rework, decompose (require per-ticket setup)
        if (verbKind is CliVerbKind.Rework or CliVerbKind.Decompose)
        {
            var singleTicketId = ticketIds[0];
            var sessionId = Guid.NewGuid().ToString("N");
            var fileStem = SessionFileNameBuilder.Build(
                projectName: config.Ticketing.PlaneProjectName,
                projectIdentifier: config.Ticketing.PlaneProjectIdentifier,
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
                BaseDirectory = ResolveLogDir(config.Events.LogDirectory),
                SessionId = sessionId,
                FileNameStem = fileStem
            }, sessionContext);
            bool enableDigest = !debugMode
                && !quietMode
                && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1");

            var eventSink = new RecordingEventSink(jsonlEventSink);
            var buildOptions = new BuildOptions(
                SessionId: sessionId,
                WorkerName: config.Workers.DefaultAgent,
                WorkerTimeout: TimeSpan.FromMinutes(config.Workers.TimeoutMinutes),
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode ? Console.Out : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null,
                TargetBranch: config.ResolveTargetBranch(),
                BuildVersion: BuildVersion.Current);

            string PlaneUrl() => BuildPlaneUrl(config.Ticketing.PlaneBaseUrl, config.Ticketing.PlaneWorkspaceSlug, singleTicketId);
            string? ArtifactsPath() => debugCaptureDir is not null
                ? $".build/sessions/{fileStem}/"
                : $".build/events/{fileStem}.jsonl";

            if (verbKind == CliVerbKind.Rework)
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

                var retriever = new ReviewFeedbackRetriever(ResolveLogDir(config.Events.LogDirectory));

                var reworkPhase = new ReworkPhase(
                    ticketing,
                    workerFactory.Create(EffectiveAgentFor("implement")),
                    eventSink,
                    buildOptions,
                    retriever,
                    reworkPhaseOptions,
                    gitClient: new ThroughlineBuild.Git.ProcessGitClient(cwd),
                    project: config.Project);

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
            else if (verbKind == CliVerbKind.Decompose)
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

    // Extracted from RunAsync to reduce the async state-machine size for Native AOT compilation.
    // action: 0=continue foreach loop, 1=break foreach loop, 2=return directly from RunAsync.
    static async Task<(int code, int action)> RunTicketVerbBodyAsync(
        string verb,
        CliVerbKind verbKind,
        string ticketId,
        string[] args,
        string cwd,
        PlaneTicketingClient ticketing,
        WorkerAgentFactory workerFactory,
        BuildConfig config,
        string logDir,
        SessionContext sessionContext,
        bool debugMode,
        bool quietMode,
        bool summaryJson,
        bool errorLocation,
        bool noAutoMerge,
        bool noAutoResolve,
        bool continuePastFailure,
        bool fromBrief,
        bool noPush,
        bool skipBaseline,
        bool chainDryRun,
        string? chainMaxDepth,
        Func<string, string> effectiveAgentFor,
        IReadOnlyList<string>? batchImplementTicketIds,
        bool batchImplementAllChildren = false)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var fileStem = SessionFileNameBuilder.Build(
            projectName: config.Ticketing.PlaneProjectName,
            projectIdentifier: config.Ticketing.PlaneProjectIdentifier,
            verb: verb,
            ticketId: ticketId,
            extraSlug: null,
            timestamp: DateTimeOffset.Now);

        string? debugCaptureDir = debugMode
            ? Path.GetFullPath(Path.Combine(cwd, ".build", "sessions", fileStem))
            : null;
        if (debugCaptureDir is not null)
            Directory.CreateDirectory(debugCaptureDir);

        await using var jsonlEventSink = new JsonlEventSink(new EventLogOptions
        {
            BaseDirectory = logDir,
            SessionId = sessionId,
            FileNameStem = fileStem
        }, sessionContext);
        bool enableDigest = !debugMode
            && !quietMode
            && (!Console.IsErrorRedirected || Environment.GetEnvironmentVariable("BUILD_PROGRESS") == "1");

        var eventSink = new RecordingEventSink(jsonlEventSink);
        // The config mode controls planning inside chain. A direct `build plan` always investigates
        // unless the operator explicitly supplies --from-brief for deterministic promotion.
        bool effectivePromotePlan = PlanDispatchPolicy.ShouldPromote(verb, fromBrief, config.Plan.IsPromote);
        var buildOptions = new BuildOptions(
            SessionId: sessionId,
            WorkerName: config.Workers.DefaultAgent,
            WorkerTimeout: TimeSpan.FromMinutes(config.Workers.TimeoutMinutes),
            DebugCaptureDirectory: debugCaptureDir,
            LiveStdoutSink: debugMode && !summaryJson ? Console.Out : null,
            LiveStderrSink: debugMode ? Console.Error : null,
            ProgressDigestSink: enableDigest ? Console.Error : null,
            TargetBranch: config.ResolveTargetBranch(),
            PromotePlan: effectivePromotePlan,
            BatchMaxTickets: config.Batch.MaxTickets,
            BatchMaxSizeScore: config.Batch.MaxSizeScore,
            BatchMaxDescriptionBytes: config.Batch.MaxDescriptionBytes,
            BuildVersion: BuildVersion.Current);

        string planeUrl = BuildPlaneUrl(config.Ticketing.PlaneBaseUrl, config.Ticketing.PlaneWorkspaceSlug, ticketId);
        string? artifactsPath = debugCaptureDir is not null
            ? $".build/sessions/{fileStem}/"
            : $".build/events/{fileStem}.jsonl";

        void WriteSummaryLocal(PhaseSummary summary)
        {
            var text = summaryJson
                ? PhaseSummaryRenderer.RenderJson(summary)
                : PhaseSummaryRenderer.RenderText(summary);
            Console.Out.Write(text);
            if (!text.EndsWith('\n')) Console.Out.WriteLine();
        }

        if (verbKind == CliVerbKind.Plan)
        {
            var phase = new PlanPhase(ticketing, workerFactory.Create(effectiveAgentFor("plan")), eventSink, buildOptions, project: config.Project, diagnostics: Console.Error);
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
                return (2, 1);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, 1);
            }

            Ticket? planTicket = null;
            try { planTicket = await ticketing.GetAsync(ticketId, CancellationToken.None); }
            catch { /* best effort */ }

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
                planeUrl: planeUrl,
                sessionArtifactsPath: artifactsPath);
            WriteSummaryLocal(planSummary);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Plan phase failed: {result.FailureReason}");
                if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
                return (1, 1);
            }
            if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            return (0, 1);
        }

        if (verbKind == CliVerbKind.Implement)
        {
            var phase = new ImplementPhase(ticketing, workerFactory.Create(effectiveAgentFor("implement")), eventSink, buildOptions, project: config.Project);
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
                return (2, 1);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, 1);
            }

            IReadOnlyList<DiffEntry> implDiff = Array.Empty<DiffEntry>();
            IReadOnlyList<string> implCommits = Array.Empty<string>();
            int implCommitCount = 0;
            if (result.Success && !string.IsNullOrEmpty(result.BranchName))
            {
                try
                {
                    var implGit = new ProcessGitClient(cwd);
                    var (baseRef, _) = await ThroughlineBuild.Git.BaseRefResolver.ResolveAsync(implGit, cwd, buildOptions.TargetBranch, CancellationToken.None);
                    var d = await implGit.DiffAsync(baseRef, result.BranchName!, cwd, includePatchContent: false, CancellationToken.None);
                    implDiff = d.Entries;
                    implCommitCount = await implGit.RevListCountAsync($"{baseRef}..{result.BranchName}", cwd, CancellationToken.None);
                    implCommits = await implGit.LogOnelineAsync($"{baseRef}..{result.BranchName}", 10, cwd, CancellationToken.None);
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
                planeUrl: planeUrl,
                sessionArtifactsPath: artifactsPath);
            WriteSummaryLocal(implSummary);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Implement phase failed: {result.FailureReason}");
                if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
                return (1, 1);
            }
            if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            return (0, 1);
        }

        if (verbKind == CliVerbKind.Review)
        {
            var verifierWorkerOptions = new WorkerOptions(
                TimeSpan.FromMinutes(config.Review.VerifierTimeoutMinutes),
                config.Review.VerifierAllowedTools,
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode && !summaryJson ? Console.Out : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null,
                DebugTranscript: new DebugTranscriptContext(
                    BuildVersion: buildOptions.BuildVersion, SessionId: buildOptions.SessionId));
            var reviewToolWarning = VerifierToolEnforcement.UnenforcedWarning(effectiveAgentFor("review"), config.Review.VerifierAllowedTools);
            if (reviewToolWarning is not null) Console.Error.WriteLine($"[build] {reviewToolWarning}");
            var reviewOptions = new ReviewOptions(config.Review.Checks, verifierWorkerOptions);
            var phase = new ReviewPhase(ticketing, workerFactory.Create(effectiveAgentFor("review")), eventSink, buildOptions, reviewOptions, project: config.Project, diagnostics: Console.Error);
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
                return (2, 1);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, 1);
            }

            var reviewSummary = PhaseSummaryBuilder.BuildReview(
                ticketId: result.TicketId,
                success: result.Success,
                verdict: result.Verdict?.ToString(),
                rationale: result.VerdictRationale,
                checksFailed: result.ChecksFailed,
                failureReason: result.FailureReason,
                events: eventSink.Snapshot(),
                planeUrl: planeUrl,
                sessionArtifactsPath: artifactsPath);
            WriteSummaryLocal(reviewSummary);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Review phase failed: {result.FailureReason}");
                if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
                return (1, 1);
            }
            if (debugCaptureDir is not null) Console.WriteLine($"Debug capture: .build/sessions/{fileStem}/");
            return (0, 1);
        }

        if (verbKind == CliVerbKind.Ship)
        {
            var shipBaselineCache = new BaselineCache();
            var shipOptions = new ShipOptions(
                RegressionChecks: config.Ship.RegressionChecks,
                Remote: config.Ship.Remote,
                BaseBranch: config.Ship.BaseBranch,
                DeleteFeatureBranch: config.Ship.DeleteFeatureBranch,
                NoAutoMerge: noAutoMerge,
                TargetBranch: config.ResolveTargetBranch(),
                NoPush: noPush || !config.Ship.Push,
                TargetBranchOverridden: config.TargetBranchOverridden,
                SkipBaseline: skipBaseline,
                BaselineCache: skipBaseline ? null : shipBaselineCache);
            var gitClient = new ProcessGitClient(cwd);
            var checksRunner = new AutomatedChecksRunner();
            var shipProgress = quietMode || summaryJson ? null : Console.Error;
            var phase = new ShipPhase(ticketing, eventSink, buildOptions, shipOptions, gitClient: gitClient, checksRunner: checksRunner, progressWriter: shipProgress, verbose: debugMode,
                baselineProber: new GateControlProber(), diagnostics: Console.Error);
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
                return (2, 1);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, 1);
            }

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
                    var shipGit = new ProcessGitClient(cwd);
                    string ontoRef;
                    var summaryRemoteExists = await shipGit.RemoteExistsAsync(config.Ship.Remote, cwd, CancellationToken.None);
                    if (summaryRemoteExists)
                        ontoRef = $"{config.Ship.Remote}/{config.Ship.BaseBranch}";
                    else
                        ontoRef = config.Ship.BaseBranch;
                    var d = await shipGit.DiffAsync(ontoRef, result.MergedSha!, cwd, includePatchContent: false, CancellationToken.None);
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
                planeUrl: planeUrl,
                sessionArtifactsPath: artifactsPath);
            WriteSummaryLocal(shipSummary);

            if (result.Success)
                return (0, 1);

            var failedAt = result.FailedAt;
            var stageName = failedAt?.ToString().ToLowerInvariant() ?? "unknown";
            Console.Error.WriteLine($"Ship blocked at {stageName}: {result.FailureReason}");
            int shipCode = failedAt switch
            {
                ShipFailureStage.Rebase => 1,
                ShipFailureStage.ConflictMarkerScan => 1,
                ShipFailureStage.RegressionChecks => 1,
                ShipFailureStage.StateCheck => 4,
                ShipFailureStage.Fetch => 4,
                ShipFailureStage.FastForwardMerge => 4,
                ShipFailureStage.Decruft => 0,
                _ => 1
            };
            return (shipCode, 1);
        }

        if (verbKind == CliVerbKind.Chain)
        {
            var (chainCode, chainDirect) = await RunChainVerbAsync(
                ticketId, args, cwd, ticketing, eventSink, buildOptions, config,
                workerFactory, debugMode, debugCaptureDir, enableDigest,
                noAutoMerge, noAutoResolve, continuePastFailure, fromBrief, noPush, skipBaseline, chainDryRun, chainMaxDepth, effectiveAgentFor,
                batchImplementTicketIds, batchImplementAllChildren);
            // chainDirect=true means return from RunAsync; false means set dispatchExitCode + break
            return (chainCode, chainDirect ? 2 : 1);
        }

        return (0, 0); // unrecognized verb - continue loop (no-op)
    }

    static async Task<(int code, bool direct)> RunChainVerbAsync(
        string ticketId,
        string[] args,
        string cwd,
        PlaneTicketingClient ticketing,
        RecordingEventSink eventSink,
        BuildOptions buildOptions,
        BuildConfig config,
        WorkerAgentFactory workerFactory,
        bool debugMode,
        string? debugCaptureDir,
        bool enableDigest,
        bool noAutoMerge,
        bool noAutoResolve,
        bool continuePastFailure,
        bool fromBrief,
        bool noPush,
        bool skipBaseline,
        bool chainDryRun,
        string? chainMaxDepth,
        Func<string, string> effectiveAgentFor,
        IReadOnlyList<string>? batchImplementTicketIds,
        bool batchImplementAllChildren = false)
    {
        var parsedMaxDepth = 16;
        // The chain verb emits no --summary-json envelope: WriteSummary/WriteSummaryLocal are called
        // only from the decompose/plan/implement/review/ship branches, never from here. Suppressing
        // the human writer under that flag would therefore silence the verb with nothing to replace
        // it (a silent `chain --dry-run --summary-json` in particular). Chain human output stays on
        // stdout unconditionally; there is no envelope on this path for it to contaminate.
        TextWriter chainHumanOutput = Console.Out;
        if (!string.IsNullOrWhiteSpace(chainMaxDepth) &&
            (!int.TryParse(chainMaxDepth, out parsedMaxDepth) || parsedMaxDepth < 0))
        {
            Console.Error.WriteLine("Error: --max-depth must be a non-negative integer");
            return (2, true);
        }

        // Collect additional positional ticket IDs beyond args[1] (args[0] is the verb).
        var extraTicketIds = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                extraTicketIds.Add(args[i]);
        }

        // Construct per-phase factories for ChainPhase.
        var planPhaseFactory = (BuildOptions buildOpts) =>
            new PlanPhase(ticketing, workerFactory.Create(effectiveAgentFor("plan")), eventSink, buildOpts, project: config.Project, diagnostics: Console.Error);

        var implementPhaseFactory = (BuildOptions buildOpts, ImplementPhaseOptions implOpts) =>
            new ImplementPhase(ticketing, workerFactory.Create(effectiveAgentFor("implement")), eventSink, buildOpts, project: config.Project, phaseOptions: implOpts);

        // Gate factory: runs the configured review checks once on the warm worktree after implement.
        // Uses the same check set as review so the relocation is transparent to the operator.
        // One shared prover instance per chain run so its per-check-once state persists across every
        // gate invocation (each gating check is probed at most once per chain). Null disables the probe.
        var gateVacuityProver = config.Review.VerifyGateVacuity ? new GateVacuityProver() : null;
        // TLB-538: environment-failure classification. The control prober re-runs failed gating
        // checks against the untouched base ref so a broken environment is never attributed to the
        // ticket; the reloader re-reads the gate checks from disk (config is otherwise loaded once
        // at startup) so a config fixed mid-run recovers without restarting the chain. Best-effort:
        // any reload failure returns null and the gate behaves as if nothing changed.
        // Also shared with the chain's ship phases as the baseline contradiction re-check prober.
        var gateControlProber = new GateControlProber();
        Func<IReadOnlyList<CheckSpec>?> gateChecksReloader = () =>
        {
            try
            {
                var freshPath = BuildConfigLoader.FindConfigFile(cwd);
                if (freshPath is null) return null;
                var freshConfig = BuildConfigLoader.Load(freshPath, branchExists: _ => true);
                return freshConfig.Review.Checks;
            }
            catch { return null; }
        };
        var gatePhaseFactory = (BuildOptions buildOpts) =>
        {
            var gateOptions = new GateOptions(config.Review.Checks);
            return new GatePhase(ticketing, eventSink, buildOpts, gateOptions, vacuityProver: gateVacuityProver,
                controlProber: gateControlProber, gateChecksReloader: gateChecksReloader, diagnostics: Console.Error);
        };

        var reviewPhaseFactory = (BuildOptions buildOpts, GateOutcome? gateOutcome) =>
        {
            var verifierWorkerOptions = new WorkerOptions(
                TimeSpan.FromMinutes(config.Review.VerifierTimeoutMinutes),
                config.Review.VerifierAllowedTools,
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode ? chainHumanOutput : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null,
                DebugTranscript: new DebugTranscriptContext(
                    BuildVersion: buildOpts.BuildVersion, SessionId: buildOpts.SessionId));
            var reviewOptions = new ReviewOptions(config.Review.Checks, verifierWorkerOptions);
            // When the gate ran its checks, pass its results to ReviewPhase so it reuses them
            // rather than running the checks a second time (one build per ticket).
            var checksRunner = gateOutcome is not null
                ? (AutomatedChecksRunner)new PreComputedChecksRunner(gateOutcome.CheckResults)
                : null;
            var preRunSmokeSignals = gateOutcome?.SmokeSignals;
            return new ReviewPhase(ticketing, workerFactory.Create(effectiveAgentFor("review")), eventSink, buildOpts, reviewOptions,
                checksRunner: checksRunner, preRunSmokeSignals: preRunSmokeSignals, project: config.Project, diagnostics: Console.Error);
        };
        // Once-per-chain honesty warning when the review worker won't enforce verifier_allowed_tools.
        var chainReviewToolWarning = VerifierToolEnforcement.UnenforcedWarning(effectiveAgentFor("review"), config.Review.VerifierAllowedTools);
        if (chainReviewToolWarning is not null) Console.Error.WriteLine($"[build] {chainReviewToolWarning}");

        // Shared baseline cache for all ship invocations within this chain: pays once per chain
        // invocation and reuses across all tickets in the chain as long as the SHA matches.
        var chainBaselineCache = new BaselineCache();

        var shipPhaseFactory = (BuildOptions buildOpts) =>
        {
            var shipOptions = new ShipOptions(
                RegressionChecks: config.Ship.RegressionChecks,
                Remote: config.Ship.Remote,
                BaseBranch: config.Ship.BaseBranch,
                DeleteFeatureBranch: config.Ship.DeleteFeatureBranch,
                NoAutoMerge: noAutoMerge,
                TargetBranch: buildOpts.TargetBranch,
                NoPush: noPush || !config.Ship.Push,
                TargetBranchOverridden: config.TargetBranchOverridden,
                SkipBaseline: skipBaseline,
                BaselineCache: skipBaseline ? null : chainBaselineCache);
            var gitClient = new ProcessGitClient(cwd);
            var checksRunner = new AutomatedChecksRunner();
            return new ShipPhase(ticketing, eventSink, buildOpts, shipOptions, gitClient: gitClient, checksRunner: checksRunner, progressWriter: buildOpts.ProgressDigestSink, verbose: debugMode,
                baselineProber: gateControlProber, diagnostics: Console.Error);
        };

        var ratifierFactory = (BuildOptions ratifyBuildOpts) =>
        {
            var ratifierWorkerOptions = new WorkerOptions(
                TimeSpan.FromMinutes(config.Review.VerifierTimeoutMinutes),
                config.Review.VerifierAllowedTools,
                DebugCaptureDirectory: debugCaptureDir,
                LiveStdoutSink: debugMode ? chainHumanOutput : null,
                LiveStderrSink: debugMode ? Console.Error : null,
                ProgressDigestSink: enableDigest ? Console.Error : null,
                DebugTranscript: new DebugTranscriptContext(
                    BuildVersion: ratifyBuildOpts.BuildVersion, SessionId: ratifyBuildOpts.SessionId));
            return (IObsoleteRatifier)new ObsoleteRatifier(
                workerFactory.Create(effectiveAgentFor("review")),
                ratifierWorkerOptions,
                cwd,
                git: new ProcessGitClient(cwd));
        };

        // Ship factory used within parent-chain accumulation: honors the phase target
        // branch so leaves ship into the current integration branch.
        var chainShipPhaseFactory = (BuildOptions buildOpts) =>
        {
            var chainShipOptions = new ShipOptions(
                RegressionChecks: config.Ship.RegressionChecks,
                Remote: config.Ship.Remote,
                BaseBranch: config.Ship.BaseBranch,
                DeleteFeatureBranch: false,
                NoAutoMerge: noAutoMerge,
                TargetBranch: buildOpts.TargetBranch,
                SkipDecruft: true,
                // Integration-branch ships are local scaffolding: their target (chain/{parent})
                // only exists locally, so they must never touch the remote. The accumulated work
                // is pushed once, when the root chain lands its integration branch onto the
                // configured target (see ChainPhase landing remote/push wiring below).
                NoPush: true,
                TargetBranchOverridden: config.TargetBranchOverridden,
                SkipBaseline: skipBaseline,
                BaselineCache: skipBaseline ? null : chainBaselineCache);
            var gitClient = new ProcessGitClient(cwd);
            var checksRunner = new AutomatedChecksRunner();
            return new ShipPhase(ticketing, eventSink, buildOpts, chainShipOptions, gitClient: gitClient, checksRunner: checksRunner, progressWriter: buildOpts.ProgressDigestSink, verbose: debugMode,
                baselineProber: gateControlProber, diagnostics: Console.Error);
        };

        // Wire the chain phase through the composition helper so the construction has a single
        // test-coverable seam (see ChainPhaseComposition). The batchWorker - omitted from the inline
        // construction for the feature's whole life (TLB bug 1) - is created inside the helper from the
        // implement agent, and a Cli.Tests check now fails if it or another required dependency is
        // dropped. Pure extraction: identical arguments to the prior inline new ChainPhase(...).
        var chainPhase = ChainPhaseComposition.BuildChainPhase(
            ticketing,
            eventSink,
            buildOptions,
            planPhaseFactory,
            implementPhaseFactory,
            reviewPhaseFactory,
            shipPhaseFactory,
            chainShipPhaseFactory,
            ratifierFactory,
            workingDirectory: cwd,
            workerFactory,
            effectiveAgentFor,
            landingRemote: config.Ship.Remote,
            landingPushEnabled: !(noPush || !config.Ship.Push),
            output: chainHumanOutput,
            diagnostics: Console.Error,
            gateFactory: gatePhaseFactory,
            // Post-rework check re-run uses the same check set the gate and review run, so the
            // recheck's verdict on a named check matches what the gate would conclude later.
            reworkRecheckSpecs: config.Review.Checks);

        ChainBatchImplementGroup? batchImplementGroup = null;
        if (batchImplementAllChildren)
        {
            // Bare --batch-implement flag: batch all eligible children discovered at runtime.
            batchImplementGroup = new ChainBatchImplementGroup.AllEligibleChildren();
        }
        else if (batchImplementTicketIds is { Count: > 0 })
        {
            var validation = await ValidateBatchImplementGroupAsync(ticketing, ticketId, batchImplementTicketIds, CancellationToken.None)
                .ConfigureAwait(false);
            if (validation.Error is not null)
            {
                Console.Error.WriteLine(validation.Error);
                return (2, true);
            }

            batchImplementGroup = validation.Group;
        }

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
                return (2, true);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, true);
            }

            // Fetch each dispatched ticket's relations, then build the blocked_by dependency graph.
            // ChainDependencyGraph normalizes CLI ids ("92") against relation targets ("TLB-92") so
            // ordering forms regardless of which id form was typed.
            var relationsByTicketId =
                new Dictionary<string, IReadOnlyList<ThroughlineBuild.Contracts.Models.Relation>>();
            try
            {
                using var relCts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; relCts.Cancel(); };
                foreach (var id in allTicketIds)
                    relationsByTicketId[id] =
                        await ticketing.GetRelationsAsync(id, relCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return (1, true);
            }

            var graph = ThroughlineBuild.Phases.ChainDependencyGraph.Build(allTicketIds, relationsByTicketId);

            var dispatcher = new ThroughlineBuild.Phases.ParallelDispatcher(
                chainPhase,
                eventSink,
                config.Workers.MaxConcurrency,
                chainHumanOutput);

            var baseChainOptions = new ThroughlineBuild.Phases.ChainPhaseOptions(
                TicketId: ticketId,
                Debug: debugMode,
                NoAutoResolve: noAutoResolve,
                BatchImplementGroup: batchImplementGroup,
                DryRun: chainDryRun,
                MaxDepth: parsedMaxDepth);

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
                return (1, true);
            }

            // Print per-ticket summary
            foreach (var r in dispatchResult.Results)
            {
                var durationMs = (long)r.TotalDuration.TotalMilliseconds;
                chainHumanOutput.WriteLine($"[{r.TicketId}] {r.Outcome} ({durationMs}ms)");
            }

            return (ChainExitCodeMapper.GetExitCode(dispatchResult), true);
        }

        // Single-ticket path (original behavior).
        var chainRunner = new DefaultChainRunner(chainPhase);
        var chainCommand = new ChainCommand(chainRunner, ticketing, chainHumanOutput);

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
                            ["no-auto-resolve"] = noAutoResolve ? "true" : "false",
                            ["dry-run"] = chainDryRun ? "true" : "false",
                            ["max-depth"] = chainMaxDepth ?? "",
                            ["batch-implement"] = batchImplementGroup switch
                            {
                                null => "",
                                ChainBatchImplementGroup.AllEligibleChildren => "*",
                                ChainBatchImplementGroup.ExplicitList e => string.Join(",", e.TicketIds),
                                _ => ""
                            }
                        });
                        var singleCommand = new ChainCommand(chainRunner, ticketing, chainHumanOutput);
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

                ChainCommand.PrintAggregateReport(allResults, chainHumanOutput);

                // Exit 0 if all results are success or skipped; non-zero if any failed.
                bool allGood = allResults.All(r =>
                    r.Outcome == ChainOutcome.Completed
                    || r.Outcome == ChainOutcome.RatifiedObsolete
                    || r.Outcome == ChainOutcome.ParentCompleted
                    || r.Outcome == ChainOutcome.DryRunPreview
                    || r.Outcome == ChainOutcome.Skipped);
                return (allGood ? 0 : 1, true);
            }

            // Single-ticket path.
            var chainCtx = new TicketCommandContext(ticketId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["debug"] = debugMode ? "true" : "false",
                ["no-auto-resolve"] = noAutoResolve ? "true" : "false",
                ["dry-run"] = chainDryRun ? "true" : "false",
                ["max-depth"] = chainMaxDepth ?? "",
                ["batch-implement"] = batchImplementGroup switch
                {
                    null => "",
                    ChainBatchImplementGroup.AllEligibleChildren => "*",
                    ChainBatchImplementGroup.ExplicitList e => string.Join(",", e.TicketIds),
                    _ => ""
                }
            });
            var cmdResult = await chainCommand.ExecuteAsync(chainCtx, cts.Token).ConfigureAwait(false);

            if (!cmdResult.Success)
            {
                // Map ChainResult.Outcome to exit code.
                if (chainCommand.LastChainResult is not null)
                {
                    return (ChainExitCodeMapper.GetExitCode(chainCommand.LastChainResult), false);
                }
                // Unhandled exception path: LastChainResult not set because ChainCommand
                // caught an exception before completing the chain. Print the message so
                // the operator can see what went wrong instead of a silent exit code 1.
                if (!string.IsNullOrEmpty(cmdResult.Message))
                    Console.Error.WriteLine($"Error: {cmdResult.Message}");
                return (1, false);
            }

            return (0, false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return (1, false);
        }
    }

    // Builds the Plane work-item deep-link URL using the ?next_path= redirect parameter.
    // Returns an empty string when any argument is unset so callers can omit the URL line.
    static string BuildPlaneUrl(string planeBaseUrl, string workspaceSlug, string ticketId)
    {
        if (string.IsNullOrEmpty(planeBaseUrl) || string.IsNullOrEmpty(workspaceSlug) || string.IsNullOrEmpty(ticketId))
            return string.Empty;
        return $"{planeBaseUrl.TrimEnd('/')}/?next_path=/{workspaceSlug}/browse/{ticketId}";
    }

    static async Task<(ChainBatchImplementGroup? Group, string? Error)> ValidateBatchImplementGroupAsync(
        ITicketing ticketing,
        string chainTicketId,
        IReadOnlyList<string> batchTicketIds,
        CancellationToken ct)
    {
        if (batchTicketIds.Count == 0)
            return (null, "Error: --batch-implement requires a non-empty comma-separated ticket list");

        Ticket parentTicket;
        IReadOnlyList<Ticket> batchTickets;
        try
        {
            parentTicket = await ticketing.GetAsync(chainTicketId, ct).ConfigureAwait(false);
            batchTickets = await ticketing.GetBatchAsync(batchTicketIds, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return (null, $"Ticket not found: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Error: failed to validate --batch-implement tickets: {ex.Message}");
        }

        var ticketsById = batchTickets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in batchTicketIds)
        {
            if (!ticketsById.ContainsKey(id))
                return (null, $"Ticket not found: {id}");
        }

        var nonSiblingIds = batchTicketIds
            .Select(id => ticketsById[id])
            .Where(t => !string.Equals(t.ParentId, parentTicket.Uuid, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToList();
        if (nonSiblingIds.Count > 0)
        {
            return (null,
                "Error: --batch-implement tickets must be direct children of " +
                $"{chainTicketId}; not siblings in that group: {string.Join(", ", nonSiblingIds)}");
        }

        return (new ChainBatchImplementGroup.ExplicitList(batchTicketIds.ToArray()), null);
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
        CliVerbKind verbKind,
        TicketCommandRegistry registry,
        BuildSecrets secrets,
        LlmConfig llmConfig,
        HttpClient http,
        ITicketing ticketing,
        IEventSink eventSink,
        string mainWorktreePath)
    {
        if (verbKind is not (CliVerbKind.Close or CliVerbKind.Defer or CliVerbKind.Reopen))
            return null;

        // close/defer/reopen use the LLM only to translate the reason text to English,
        // which is non-essential. If no real client can be built (e.g. no API key),
        // fall back to an echo client so the ticket transition still runs - the reason
        // is recorded verbatim. Do NOT abort: these are deterministic state commands.
        ILlmClient llmClient;
        try
        {
            llmClient = LlmClientFactory.Create(llmConfig, secrets, http);
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine(
                $"WARNING: LLM unavailable ({ex.Message}); recording reason verbatim without translation.");
            llmClient = new EchoLlmClient();
        }
        var translator = new ReasonTranslator(llmClient);

        if (verbKind == CliVerbKind.Close)
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
        else if (verbKind == CliVerbKind.Defer)
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
        else if (verbKind == CliVerbKind.Reopen)
        {
            registry.Register("reopen", new ReopenCommand(
                ticketing,
                eventSink,
                translator));
        }

        return null;
    }

}
