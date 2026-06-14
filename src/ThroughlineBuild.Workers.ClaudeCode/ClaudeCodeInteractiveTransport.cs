using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal interface IClaudeCompletionWaiter
{
    Task<ClaudeCompletionRecord> WaitAsync(ClaudeRunDirectory run, CancellationToken cancellationToken);
}

internal sealed class ClaudeCompletionWaiter : IClaudeCompletionWaiter
{
    private readonly ClaudeCompletionStore _store = new();

    public Task<ClaudeCompletionRecord> WaitAsync(ClaudeRunDirectory run, CancellationToken cancellationToken) =>
        _store.WaitAsync(run, cancellationToken);
}

internal sealed class ClaudeCodeInteractiveTransport : IClaudeCodeTransport
{
    private const string InitialPrompt =
        "Read .build/brief.md, execute it completely, and obey the brief's final-output contract.";

    // The initial prompt carries the run id verbatim as a correlation nonce. claude
    // persists the prompt into the transcript's user message, so the locator can require
    // this token to identify THIS run's transcript and reject a stale prior-run
    // transcript in the same worktree (same cwd) or a competing session's file. The hex
    // run id JSON-encodes with no escaping, so a raw substring search of the transcript
    // always finds it. Kept on its own clearly-ignorable line so it never perturbs the
    // brief's instructions or output contract.
    internal static string BuildInitialPrompt(string runNonce) =>
        $"{InitialPrompt}\n(latticeflow run token, ignore: {runNonce})";

    // A run directory with no live lock is reclaimed regardless of age; this bound
    // only governs legacy/partial directories that predate the lock file.
    private static readonly TimeSpan StaleRunMinimumAge = TimeSpan.FromHours(1);

    // Best-effort graceful nudge sent to the pty once the assistant turn is done so
    // claude exits cleanly where it can. Completion does NOT depend on this: the
    // transport synthesizes the completion from the located transcript and then
    // terminates the tree regardless of whether /exit lands or the Stop hook fires.
    private const string ExitCommand = "/exit\r";

    private readonly ClaudeCodeOptions _options;
    private readonly IInteractiveClaudeProcessLauncher _launcher;
    private readonly IClaudeCompletionWaiter _completionWaiter;
    private readonly IInteractiveTurnSignal _turnSignal;
    private readonly IReadOnlyList<string>? _hookCommandPrefix;

    internal ClaudeCodeInteractiveTransport(ClaudeCodeOptions options)
        : this(options, InteractiveClaudeProcessLauncherFactory.Create(), new ClaudeCompletionWaiter(),
            new TranscriptTurnSignal()) { }

    internal ClaudeCodeInteractiveTransport(
        ClaudeCodeOptions options,
        IInteractiveClaudeProcessLauncher launcher,
        IClaudeCompletionWaiter completionWaiter,
        IInteractiveTurnSignal turnSignal,
        IReadOnlyList<string>? hookCommandPrefix = null)
    {
        _options = options;
        _launcher = launcher;
        _completionWaiter = completionWaiter;
        _turnSignal = turnSignal;
        _hookCommandPrefix = hookCommandPrefix;
    }

    public async Task<WorkerResult> ExecuteAsync(
        Brief brief,
        string workingDirectory,
        WorkerOptions options,
        CancellationToken ct)
    {
        var buildDirectory = Path.Combine(workingDirectory, ".build");
        Directory.CreateDirectory(buildDirectory);

        // Canonicalize the worktree to claude's own form (resolving symlinks) so the
        // spawn cwd, the trust key, and the turn-match all agree. macOS hands back an
        // unresolved /var/... temp path while claude records the resolved /private/var/...
        // cwd; without this the turn-detector never matches and the worker times out.
        // Resolved after Directory.CreateDirectory so the path exists for realpath.
        var canonicalWorktree = ClaudeRealPath.Resolve(workingDirectory);

        // Same-worktree collision guard: two interactive runs in one worktree would
        // race on the shared .build/brief.md path. Independent worktrees hash to
        // distinct locks, so concurrent runs there are never blocked by this.
        using var worktreeLock = InteractiveClaudeWorktreeLock.TryAcquire(workingDirectory);
        if (worktreeLock is null)
            return CollisionFailure(workingDirectory, options.DebugCaptureDirectory, brief);

        await File.WriteAllTextAsync(Path.Combine(buildDirectory, "brief.md"), brief.Instruction, ct);

        var runId = Guid.NewGuid().ToString("N");
        var preserveRun = options.DebugCaptureDirectory is not null;
        var runParent = preserveRun
            ? Path.Combine(options.DebugCaptureDirectory!, "claude-interactive-runs")
            : Path.Combine(Path.GetTempPath(), "latticeflow-claude-runs");
        // Reclaim run directories orphaned by a crashed parent before adding ours.
        // Preserved debug runs are intentionally retained and never swept.
        if (!preserveRun)
            ClaudeRunDirectorySweeper.SweepStaleRuns(runParent, StaleRunMinimumAge);
        var runPath = Path.Combine(runParent, runId);
        Directory.CreateDirectory(runPath);
        var run = ClaudeRunDirectory.Open(runPath, runId);

        IInteractiveClaudeProcess? process = null;
        ClaudeRunLease? lease = null;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Hold the lease for the whole run so a concurrent sweeper treats it as live.
            lease = ClaudeRunLease.Acquire(run);
            var settingsPath = Path.Combine(run.Path, "settings.json");
            await File.WriteAllTextAsync(
                settingsPath,
                ClaudeHookSettingsBuilder.Build(_hookCommandPrefix ?? ResolveHookCommandPrefix(), run.Path, run.RunId),
                ct);

            _options.Sizes.TryGetValue(options.Size, out var tier);
            var model = ClaudeCodeAgent.NormalizeModel(tier?.Model);
            if (ClaudeCodeModelValidator.Validate(model) is string modelError)
                return FailureWithDebug("Invalid Claude Code model", $"{modelError}. Run directory: '{run.Path}'.",
                    options.DebugCaptureDirectory, run.Path, brief);

            var arguments = BuildInteractiveArgs(_options, options, settingsPath, model, run.RunId);
            WriteProgress(options.ProgressDigestSink, startedAt, "agent", model is null
                ? "claude-code interactive"
                : $"claude-code interactive model {model}");
            var environmentInfo = new ProcessStartInfo(_options.ExecutablePath) { UseShellExecute = false };
            ClaudeCodeAgent.ConfigureEnvironment(environmentInfo, _options, options);
            var environment = environmentInfo.Environment.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            // Pre-seed workspace trust so the interactive launch does not hang at
            // claude's trust dialog on a fresh worktree (which --dangerously-skip-
            // permissions does NOT bypass). Best-effort: never throws.
            ClaudeWorkspaceTrust.TryEnsureTrusted(canonicalWorktree);

            try
            {
                process = _launcher.Launch(new InteractiveClaudeLaunchSpec(
                    _options.ExecutablePath, arguments, canonicalWorktree, environment));
            }
            catch (Win32Exception ex)
            {
                return FailureWithDebug(
                    $"Worker executable not found: '{_options.ExecutablePath}'",
                    $"Unable to launch interactive Claude Code in the terminal host: {ex.Message}. Run directory: '{run.Path}'.",
                    options.DebugCaptureDirectory, run.Path, brief);
            }
            catch (PlatformNotSupportedException ex)
            {
                return FailureWithDebug("Interactive Claude Code is unsupported on this host",
                    $"{ex.Message} Run directory: '{run.Path}'.", options.DebugCaptureDirectory, run.Path, brief);
            }

            var launchedAt = DateTimeOffset.UtcNow;
            using var waitCancellation = new CancellationTokenSource();
            var completionTask = _completionWaiter.WaitAsync(run, waitCancellation.Token);
            // The turn signal locates the run's persisted transcript and resolves with
            // its PATH once the assistant turn ends. The Stop hook is unreliable
            // cross-platform (Linux's O_NOCTTY/sdk-cli launch never fires it), so the
            // transport synthesizes the completion from this transcript rather than
            // waiting for completion.json. Bounded by the same linked token the
            // transport cancels on completion/timeout/cancel.
            var turnTask = _turnSignal.WaitForTurnAsync(canonicalWorktree, run.RunId, launchedAt, waitCancellation.Token);
            var timeoutTask = Task.Delay(options.Timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);

            // Phase 1: wait for the first of a real Stop-hook completion (fast-path,
            // backward compatible), the located transcript (synthesize the completion),
            // process exit, timeout, or cancellation.
            await Task.WhenAny(completionTask, turnTask, process.ExitTask, timeoutTask, cancellationTask);

            // A completion that resolved successfully always wins, preserving backward
            // compatibility with any claude that fires the per-turn hook. The legacy
            // Stop-hook payload reports a transcript_path whose FILENAME is unreliable,
            // so this path re-resolves the real transcript by project dir + cwd + nonce.
            if (completionTask.IsCompletedSuccessfully)
                return await HandleSuccessfulCompletionAsync(
                    completionTask, process, run, brief, options, arguments, model, stopwatch, startedAt,
                    reResolveTranscript: true);

            if (cancellationTask.IsCompleted)
            {
                waitCancellation.Cancel();
                var killFailure = await TryKillAndWaitAsync(process);
                return FailureWithDebug("Interactive Claude execution was cancelled",
                    $"Cancellation requested. {killFailure}Run directory: '{run.Path}'.",
                    options.DebugCaptureDirectory, run.Path, brief);
            }

            if (timeoutTask.IsCompleted)
            {
                waitCancellation.Cancel();
                var killFailure = await TryKillAndWaitAsync(process);
                var timeoutFailure = FailureWithDebug("Interactive Claude execution timed out",
                    $"Timed out after {options.Timeout}. {killFailure}Run directory: '{run.Path}'.",
                    options.DebugCaptureDirectory, run.Path, brief);
                // Phase-1 timeout means the turn-detector never matched (turnTask never
                // completed -> /exit never sent). Append a self-diagnosis of every
                // candidate transcript so the next live run pins the exact reason from
                // artifacts. Best-effort: never throws, never alters the result text.
                AppendTurnDiagnostics(options.DebugCaptureDirectory, canonicalWorktree, run.RunId, launchedAt);
                return timeoutFailure;
            }

            if (completionTask.IsCompleted)
            {
                waitCancellation.Cancel();
                // Faulted/cancelled completion.
                try { await completionTask; }
                catch (Exception ex)
                {
                    var killFailure = await TryKillAndWaitAsync(process);
                    return FailureWithDebug("Claude Stop-hook completion was malformed",
                        $"{ex.Message}. {killFailure}Run directory: '{run.Path}'.",
                        options.DebugCaptureDirectory, run.Path, brief);
                }
            }

            if (turnTask.IsCompletedSuccessfully && turnTask.Result is string transcriptPath)
            {
                // The assistant turn finished and the turn-detector located the run's
                // transcript. Synthesize the completion from that transcript instead of
                // waiting for the unreliable Stop hook. Send /exit first as a best-effort
                // graceful nudge so claude exits cleanly where it can - but completion
                // does NOT depend on it landing, on process exit, or on completion.json.
                waitCancellation.Cancel();
                WriteProgress(options.ProgressDigestSink, startedAt, "agent",
                    "turn complete; synthesizing completion from the located transcript");
                try { await process.WriteInputAsync(ExitCommand, ct); }
                catch
                {
                    // A failed /exit write is harmless: we terminate the tree below.
                }

                var synthesized = SynthesizeCompletion(run, canonicalWorktree, transcriptPath);
                // Synthesized completions already hold the EXACT transcript turn
                // detection located (nonce + cwd + end_turn). Consume it directly; do
                // NOT re-resolve by newest-cwd, which a concurrent MCP/session write
                // could swap out from under us between turn-detect and parse.
                return await HandleSuccessfulCompletionAsync(
                    Task.FromResult(synthesized), process, run, brief, options, arguments, model, stopwatch, startedAt,
                    reResolveTranscript: false);
            }

            // The only remaining Phase 1 winner is process exit before any signal.
            waitCancellation.Cancel();
            var exitCode = await process.ExitTask;
            return FailureWithDebug("Interactive Claude exited before trusted completion",
                $"Claude exited with code {exitCode} before the correlated Stop hook completed. Run directory: '{run.Path}'.",
                options.DebugCaptureDirectory, run.Path, brief);
        }
        finally
        {
            if (process is not null)
                await process.DisposeAsync();
            // Release the lease before deleting so the directory looks reclaimable.
            lease?.Dispose();
            if (!preserveRun)
                TryDeleteRunDirectory(run.Path);
        }
    }

    private async Task<WorkerResult> HandleSuccessfulCompletionAsync(
        Task<ClaudeCompletionRecord> completionTask,
        IInteractiveClaudeProcess process,
        ClaudeRunDirectory run,
        Brief brief,
        WorkerOptions options,
        IReadOnlyList<string> arguments,
        string? model,
        Stopwatch stopwatch,
        DateTimeOffset startedAt,
        bool reResolveTranscript)
    {
        var completion = await completionTask;
        var killFailure = await TryKillAndWaitAsync(process);
        if (killFailure is not null)
            return FailureWithDebug("Interactive Claude process cleanup failed",
                $"{killFailure}Run directory: '{run.Path}'.", options.DebugCaptureDirectory, run.Path, brief);
        if (!string.Equals(completion.RunId, run.RunId, StringComparison.Ordinal))
            return FailureWithDebug("Claude Stop-hook completion was stale",
                $"Completion run id '{completion.RunId}' did not match expected run id '{run.RunId}'. Run directory: '{run.Path}'.",
                options.DebugCaptureDirectory, run.Path, brief);
        stopwatch.Stop();
        WriteProgress(options.ProgressDigestSink, startedAt, "result", "completion resolved; recovering persisted transcript");
        return ParseCompletion(completion, run.Path, brief, options, arguments, model,
            stopwatch.ElapsedMilliseconds, startedAt, reResolveTranscript);
    }

    // Builds a completion record from the located persisted transcript when the Stop
    // hook never fires (the cross-platform reality). The transcript is read tolerantly
    // (the on-disk session id need not match any payload), and its reconstructed
    // assistant text seeds LastAssistantMessage. TranscriptPath is the located file and
    // Cwd is the canonical worktree, so ParseCompletion's locator re-resolves the same
    // file for telemetry. StopHookActive=false records that no hook drove this.
    private static ClaudeCompletionRecord SynthesizeCompletion(
        ClaudeRunDirectory run, string canonicalWorktree, string transcriptPath)
    {
        string sessionId = "transcript";
        string lastAssistantMessage = "";
        try
        {
            var transcript = ClaudePersistedTranscriptReader.Read(transcriptPath, sessionId, validateSessionId: false);
            if (!string.IsNullOrWhiteSpace(transcript.SessionId)) sessionId = transcript.SessionId!;
            lastAssistantMessage = transcript.AssistantTranscript;
        }
        catch
        {
            // A read failure here is non-fatal: ParseCompletion re-reads the transcript
            // (resolving telemetry) and reports the parse/telemetry error itself. The
            // placeholder session id and empty message keep the record well-formed.
        }
        return new ClaudeCompletionRecord(
            ClaudeCompletionStore.CurrentSchemaVersion,
            run.RunId,
            sessionId,
            canonicalWorktree,
            transcriptPath,
            lastAssistantMessage,
            StopHookActive: false,
            DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<string> BuildInteractiveArgs(
        ClaudeCodeOptions claudeOptions,
        WorkerOptions workerOptions,
        string settingsPath,
        string? model,
        string runNonce)
    {
        var arguments = new List<string>();
        if (claudeOptions.BypassPermissions)
        {
            arguments.Add("--dangerously-skip-permissions");
            arguments.AddRange(["--permission-mode", "bypassPermissions"]);
        }
        if (workerOptions.AllowedTools is { Count: > 0 })
            arguments.AddRange(["--allowedTools", string.Join(",", workerOptions.AllowedTools)]);
        if (workerOptions.LeanPlanning)
            arguments.AddRange(["--disallowedTools", "TodoWrite,Task"]);
        if (model is not null)
            arguments.AddRange(["--model", model]);
        arguments.AddRange(claudeOptions.ExtraArgs);
        arguments.AddRange(["--settings", settingsPath, BuildInitialPrompt(runNonce)]);
        return arguments;
    }

    private static WorkerResult ParseCompletion(
        ClaudeCompletionRecord completion,
        string runPath,
        Brief brief,
        WorkerOptions options,
        IReadOnlyList<string> arguments,
        string? fallbackModel,
        long wallClockMs,
        DateTimeOffset startedAt,
        bool reResolveTranscript)
    {
        ClaudePersistedTranscript? transcript = null;
        string? telemetryError = null;
        try
        {
            string transcriptPath;
            bool validateSessionId;
            if (reResolveTranscript)
            {
                // Legacy Stop-hook path only: claude 2.1.177's Stop-hook payload reports
                // a transcript_path whose FILENAME (session id) does not match the actual
                // on-disk transcript for the same run. The DIRECTORY part is correct, so
                // resolve the real file by the project dir + newest *.jsonl whose content
                // cwd matches AND that carries this run's nonce - so a concurrent MCP or
                // session cannot substitute a different file. Fall back to the payload
                // path if nothing resolves; then the on-disk session id must match.
                var projectDir = Path.GetDirectoryName(completion.TranscriptPath);
                var resolved = projectDir is null
                    ? null
                    : ClaudeTranscriptLocator.FindNewestMatching(projectDir, completion.Cwd, completion.RunId, notBefore: null);
                transcriptPath = resolved ?? completion.TranscriptPath;
                validateSessionId = resolved is null;
            }
            else
            {
                // Synthesized completion: TranscriptPath is the EXACT file turn detection
                // located (already nonce + cwd + end_turn correlated). Consume it directly
                // so nothing re-scanned between turn-detect and parse can replace it. The
                // synthesized session id is the on-disk one, so no validation is needed.
                transcriptPath = completion.TranscriptPath;
                validateSessionId = false;
            }
            transcript = ClaudePersistedTranscriptReader.Read(
                transcriptPath, completion.ClaudeSessionId, validateSessionId);
        }
        catch (Exception ex)
        {
            telemetryError = ex.Message;
        }

        var assistantText = string.IsNullOrWhiteSpace(transcript?.AssistantTranscript)
            ? completion.LastAssistantMessage
            : transcript.AssistantTranscript;

        if (ClaudeCodeAgent.TryDescribeInvalidModelError(assistantText, "") is string invalidModel)
            return Failure("Claude Code rejected the configured model", $"{invalidModel} Run directory: '{runPath}'.");

        var outcome = WorkerResultParser.TryParse(assistantText);
        WorkerResult result;
        if (outcome.Result is not null)
            result = outcome.Result with { Blocks = outcome.Blocks };
        else if (outcome.DeserializeErrorType is not null)
            result = Failure("Failed to deserialize WORKER_RESULT JSON",
                $"{outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}. Run directory: '{runPath}'.");
        else if (transcript?.ProviderErrorText is string providerError)
            result = new WorkerResult(Status.Escalate, "Claude Code provider failure", Array.Empty<string>(),
                $"Claude Code reported: {providerError}. Run directory: '{runPath}'.", new Dictionary<string, object>());
        else
        {
            var missing = Failure("No WORKER_RESULT found in interactive Claude completion",
                $"Claude Code response: {assistantText}. The trusted Stop-hook completion did not contain a WORKER_RESULT block. " +
                $"Run directory: '{runPath}'.");
            result = ProviderErrorClassifier.Classify(missing, "claude-code") is not null
                ? missing with { Status = Status.Escalate, Summary = "Claude Code provider failure" }
                : missing;
        }

        if (transcript is not null)
        {
            result = AttachTelemetry(result, transcript, wallClockMs, fallbackModel);
            result = ClaudeCodeAgent.AttachContextTurns(result,
                string.Join('\n', transcript.NormalizedLines.Select(line => line.Line)));
        }

        TryWriteDebugCapture(options.DebugCaptureDirectory, options.DebugTranscript, runPath, brief, completion, transcript,
            telemetryError, result, arguments, fallbackModel, wallClockMs, startedAt);
        return result;
    }

    private static WorkerResult AttachTelemetry(
        WorkerResult result,
        ClaudePersistedTranscript transcript,
        long wallClockMs,
        string? fallbackModel)
    {
        var usage = new Dictionary<string, object?>
        {
            ["model"] = transcript.Model ?? fallbackModel,
            ["vendor"] = "anthropic",
            ["wall_clock_ms"] = wallClockMs,
            ["input_tokens"] = transcript.Usage?.InputTokens,
            ["output_tokens"] = transcript.Usage?.OutputTokens,
            ["cache_read_tokens"] = transcript.Usage?.CacheReadInputTokens,
            ["cache_create_tokens"] = transcript.Usage?.CacheCreationInputTokens,
            ["partial"] = transcript.Usage is null
                || transcript.Usage.InputTokens is null
                || transcript.Usage.OutputTokens is null
                || transcript.Usage.CacheReadInputTokens is null
                || transcript.Usage.CacheCreationInputTokens is null
        };
        var merged = new Dictionary<string, object>(result.Metadata)
        {
            ["llm_usage"] = usage
        };
        return result with { Metadata = merged };
    }

    private static void TryWriteDebugCapture(
        string? captureDirectory,
        DebugTranscriptContext? debugTranscript,
        string runPath,
        Brief brief,
        ClaudeCompletionRecord completion,
        ClaudePersistedTranscript? transcript,
        string? telemetryError,
        WorkerResult result,
        IReadOnlyList<string> arguments,
        string? fallbackModel,
        long wallClockMs,
        DateTimeOffset startedAt)
    {
        if (captureDirectory is null) return;
        try
        {
            Directory.CreateDirectory(captureDirectory);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-stdin.txt"), brief.Instruction, Encoding.UTF8);
            var debugCompletionPath = Path.Combine(captureDirectory, "hook-completion.json");
            var redactedCompletion = completion with
            {
                LastAssistantMessage = ClaudePersistedTranscriptReader.RedactText(completion.LastAssistantMessage)
            };
            var completionJson = System.Text.Json.JsonSerializer.Serialize(
                redactedCompletion, ClaudeHookJsonContext.Default.ClaudeCompletionRecord);
            File.WriteAllText(debugCompletionPath, completionJson, Encoding.UTF8);
            File.WriteAllText(Path.Combine(runPath, ClaudeRunDirectory.CompletionFileName), completionJson, Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDirectory, "assistant-transcript.txt"),
                transcript?.RedactedAssistantTranscript
                    ?? ClaudePersistedTranscriptReader.RedactText(completion.LastAssistantMessage), Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDirectory, "provider-transcript.jsonl"),
                transcript?.RedactedRawTranscript ?? "", Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDirectory, "process-host.txt"),
                $"transport=interactive-hook{Environment.NewLine}terminal_rendering_parsed=false{Environment.NewLine}" +
                $"run_directory={runPath}{Environment.NewLine}telemetry_error={telemetryError ?? ""}{Environment.NewLine}", Encoding.UTF8);
            var dto = new WorkerResultDebugDto(result.Status.ToString(), result.Summary,
                result.FilesChanged, result.FailureReason);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-result.json"),
                System.Text.Json.JsonSerializer.Serialize(dto, DebugCaptureJsonContext.Default.WorkerResultDebugDto), Encoding.UTF8);
            if (transcript is not null)
                WorkerTranscriptWriter.Write(captureDirectory, brief, transcript.RedactedNormalizedLines, result,
                    context: debugTranscript, model: transcript.Model ?? fallbackModel, invocationArgs: arguments,
                    wallClockMs: wallClockMs, startedAt: startedAt);
        }
        catch
        {
            // Optional diagnostic capture must never change the worker result.
        }
    }

    // Appends the turn-detector's candidate diagnosis to the process-host.txt debug
    // artifact on the Phase-1 timeout path only. Best-effort: a null capture dir, an
    // IO failure, or a throwing diagnosis must never change the worker result.
    private void AppendTurnDiagnostics(string? captureDirectory, string worktree, string runNonce, DateTimeOffset launchedAt)
    {
        if (captureDirectory is null) return;
        try
        {
            var report = _turnSignal.DescribeCandidates(worktree, runNonce, launchedAt);
            var path = Path.Combine(captureDirectory, "process-host.txt");
            File.AppendAllText(path,
                $"turn_detector_diagnosis=begin{Environment.NewLine}{report}turn_detector_diagnosis=end{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostic capture must never affect the worker result.
        }
    }

    private static void WriteProgress(TextWriter? sink, DateTimeOffset startedAt, string kind, string message)
    {
        if (sink is null) return;
        try
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            sink.WriteLine($"[{ClaudeCodeProgressDigester.FormatOffset(elapsed)}] {kind.PadRight(10)} {message}");
        }
        catch { }
    }

    private static async Task<string?> TryKillAndWaitAsync(IInteractiveClaudeProcess process)
    {
        try
        {
            // TerminateAsync escalates graceful -> forced and waits for tree exit,
            // all internally bounded.
            await process.TerminateAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to terminate the process tree: {ex.Message}. ";
        }
    }

    private static IReadOnlyList<string> ResolveHookCommandPrefix()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current build executable path is unavailable.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return [processPath];

        var buildAssembly = Path.Combine(AppContext.BaseDirectory, "build.dll");
        if (!File.Exists(buildAssembly))
            throw new InvalidOperationException($"Current build assembly was not found at '{buildAssembly}'.");
        return [processPath, buildAssembly];
    }

    private static WorkerResult Failure(string summary, string reason) =>
        new(Status.Failed, summary, Array.Empty<string>(), reason, new Dictionary<string, object>());

    private static WorkerResult CollisionFailure(string workingDirectory, string? captureDirectory, Brief brief)
    {
        var lockPath = InteractiveClaudeWorktreeLock.PathFor(workingDirectory);
        var result = Failure(
            "Another interactive Claude run is active in this worktree",
            $"A concurrent interactive run already holds the worktree lock '{lockPath}' for '{workingDirectory}'. " +
            "Same-worktree interactive runs are not supported; run them in separate worktrees, or wait for the active run to finish.");
        if (captureDirectory is null) return result;
        try
        {
            Directory.CreateDirectory(captureDirectory);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-stdin.txt"), brief.Instruction, Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDirectory, "process-host.txt"),
                $"transport=interactive-hook{Environment.NewLine}terminal_rendering_parsed=false{Environment.NewLine}" +
                $"worktree_lock={lockPath}{Environment.NewLine}failure={result.FailureReason}{Environment.NewLine}", Encoding.UTF8);
            var dto = new WorkerResultDebugDto(result.Status.ToString(), result.Summary, result.FilesChanged, result.FailureReason);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-result.json"),
                System.Text.Json.JsonSerializer.Serialize(dto, DebugCaptureJsonContext.Default.WorkerResultDebugDto), Encoding.UTF8);
        }
        catch { }
        return result;
    }

    private static WorkerResult FailureWithDebug(
        string summary,
        string reason,
        string? captureDirectory,
        string runPath,
        Brief brief)
    {
        var result = Failure(summary, reason);
        if (captureDirectory is null) return result;
        try
        {
            Directory.CreateDirectory(captureDirectory);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-stdin.txt"), brief.Instruction, Encoding.UTF8);
            File.WriteAllText(Path.Combine(captureDirectory, "process-host.txt"),
                $"transport=interactive-hook{Environment.NewLine}terminal_rendering_parsed=false{Environment.NewLine}" +
                $"run_directory={runPath}{Environment.NewLine}failure={reason}{Environment.NewLine}", Encoding.UTF8);
            var dto = new WorkerResultDebugDto(result.Status.ToString(), result.Summary,
                result.FilesChanged, result.FailureReason);
            File.WriteAllText(Path.Combine(captureDirectory, "worker-result.json"),
                System.Text.Json.JsonSerializer.Serialize(dto, DebugCaptureJsonContext.Default.WorkerResultDebugDto), Encoding.UTF8);
        }
        catch { }
        return result;
    }

    private static void TryDeleteRunDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
