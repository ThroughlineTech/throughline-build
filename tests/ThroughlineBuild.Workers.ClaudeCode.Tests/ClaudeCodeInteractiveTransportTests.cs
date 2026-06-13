using System.Diagnostics;
using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ClaudeCodeInteractiveTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lattice interactive tests {Guid.NewGuid():N}");

    [Fact]
    public async Task CompletionBeforeExit_KillsProcessParsesFullResponseAndCleansRun()
    {
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process);
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, """
            <<<REPORT_START
            first fenced block
            <<<REPORT_END
            WORKER_RESULT
            {"status":"Ok","summary":"sentinel","files_changed":[],"failure_reason":null}
            """)));

        var result = await ExecuteAsync(launcher, waiter);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("sentinel", result.Summary);
        Assert.Equal("first fenced block", result.Blocks!["REPORT"]);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
        Assert.False(Directory.Exists(waiter.Run!.Path));
        Assert.DoesNotContain("--print", launcher.Spec!.Arguments);
        Assert.Contains("--settings", launcher.Spec.Arguments);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", launcher.Spec.Environment.Keys);
    }

    [Fact]
    public async Task ProcessExitBeforeCompletion_ReturnsActionableFailureAndCleansRun()
    {
        var process = new FakeProcess(exitCode: 17);
        var waiter = new FakeWaiter((_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ContinueWith<ClaudeCompletionRecord>(_ => throw new UnreachableException(), CancellationToken.None));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("code 17", result.FailureReason);
        Assert.Contains("Run directory", result.FailureReason);
        Assert.False(Directory.Exists(waiter.Run!.Path));
    }

    [Fact]
    public async Task CancellationConcurrentWithCompletion_TrustedCompletionWins()
    {
        var process = new FakeProcess();
        var completion = new TaskCompletionSource<ClaudeCompletionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new FakeWaiter((run, _) => completion.Task);
        using var cancellation = new CancellationTokenSource();
        var task = ExecuteAsync(new FakeLauncher(process), waiter, cancellationToken: cancellation.Token);
        var run = await waiter.RunObserved.Task;

        completion.SetResult(Completion(run.RunId, WorkerResultText("race won")));
        cancellation.Cancel();
        var result = await task;

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("race won", result.Summary);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Timeout_KillsProcessAndCleansRun()
    {
        var process = new FakeProcess();
        var waiter = NeverCompletes();

        var result = await ExecuteAsync(new FakeLauncher(process), waiter, TimeSpan.FromMilliseconds(30));

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("timed out", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.Killed);
        Assert.False(Directory.Exists(waiter.Run!.Path));
    }

    [Fact]
    public async Task TimeoutUnderDebug_WritesFailureDiagnostics()
    {
        var debugDirectory = Path.Combine(_root, "timeout-debug");
        var result = await ExecuteAsync(new FakeLauncher(new FakeProcess()), NeverCompletes(),
            TimeSpan.FromMilliseconds(30), debugDirectory: debugDirectory);

        Assert.Equal(Status.Failed, result.Status);
        Assert.True(File.Exists(Path.Combine(debugDirectory, "worker-stdin.txt")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "process-host.txt")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "worker-result.json")));
        Assert.Contains("timed out", File.ReadAllText(Path.Combine(debugDirectory, "process-host.txt")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationWithoutCompletion_KillsProcessAndCleansRun()
    {
        var process = new FakeProcess();
        var waiter = NeverCompletes();
        using var cancellation = new CancellationTokenSource();
        var task = ExecuteAsync(new FakeLauncher(process), waiter, cancellationToken: cancellation.Token);
        await waiter.RunObserved.Task;

        cancellation.Cancel();
        var result = await task;

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("cancelled", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(process.Killed);
        Assert.False(Directory.Exists(waiter.Run!.Path));
    }

    [Fact]
    public async Task MalformedCompletion_KillsProcessAndReturnsFailure()
    {
        var process = new FakeProcess();
        var waiter = new FakeWaiter((_, _) => Task.FromException<ClaudeCompletionRecord>(new InvalidDataException("partial JSON")));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("malformed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial JSON", result.FailureReason);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task CompletionFromAnotherRun_IsRejected()
    {
        var process = new FakeProcess();
        var waiter = new FakeWaiter((_, _) => Task.FromResult(Completion("stale-run", WorkerResultText("wrong"))));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("stale", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustedCompletionWithoutResultEnvelope_FailsClearly()
    {
        var process = new FakeProcess();
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, "finished without envelope")));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("No WORKER_RESULT", result.Summary);
        Assert.Contains("Run directory", result.FailureReason);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task ProviderFailureTextWithoutEnvelope_IsPreservedAndEscalated()
    {
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(
            run.RunId, "Claude AI usage limit reached|1781366400")));

        var result = await ExecuteAsync(new FakeLauncher(new FakeProcess()), waiter);

        Assert.Equal(Status.Escalate, result.Status);
        Assert.Contains("usage limit reached", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillFailure_IsReturnedInsteadOfSuccessfulEnvelope()
    {
        var process = new FakeProcess(killException: new InvalidOperationException("access denied"));
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("would pass"))));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("cleanup failed", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access denied", result.FailureReason);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task DebugRun_PreservesSettingsAndCompletionEvidence()
    {
        var debugDirectory = Path.Combine(_root, "debug");
        var transcriptPath = CopyFixture("persisted-transcript-2.1.52.jsonl");
        var process = new FakeProcess();
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(
            run.RunId, WorkerResultText("debug"), transcriptPath, "fixture-session")));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter, debugDirectory: debugDirectory);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("fixture complete", result.Summary);
        Assert.True(Directory.Exists(waiter.Run!.Path));
        Assert.True(File.Exists(Path.Combine(waiter.Run.Path, "settings.json")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "worker-stdin.txt")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "provider-transcript.jsonl")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "assistant-transcript.txt")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "hook-completion.json")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "process-host.txt")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "worker-result.json")));
        Assert.True(File.Exists(Path.Combine(debugDirectory, "transcript.jsonl")));
        Assert.DoesNotContain("fixture-secret", File.ReadAllText(Path.Combine(debugDirectory, "provider-transcript.jsonl")));
    }

    [Fact]
    public async Task PersistedTranscript_AttachesModelUsageContextAndAllFencedBlocks()
    {
        var transcriptPath = CopyFixture("persisted-transcript-2.1.52.jsonl");
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(
            run.RunId, "final message only", transcriptPath, "fixture-session")));
        var progress = new StringWriter();

        var result = await ExecuteAsync(new FakeLauncher(new FakeProcess()), waiter, progressSink: progress);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("first block", result.Blocks!["REPORT"]);
        var usage = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Metadata["llm_usage"]);
        Assert.Equal("claude-sonnet-4-6", usage["model"]);
        Assert.Equal(17, usage["input_tokens"]);
        Assert.Equal(65, usage["output_tokens"]);
        Assert.Equal(240, usage["cache_read_tokens"]);
        Assert.Equal(35, usage["cache_create_tokens"]);
        Assert.False((bool)usage["partial"]!);
        Assert.True(result.Metadata.ContainsKey("context_turns"));
        Assert.Contains("interactive", progress.ToString());
        Assert.Contains("recovering persisted transcript", progress.ToString());
    }

    [Fact]
    public async Task PayloadTranscriptFilenameMismatch_ResolvesRealTranscriptByCwdAndRecoversTelemetry()
    {
        // claude 2.1.177 reports a transcript_path whose FILENAME (session id) does not
        // match the real on-disk transcript for the same run. The DIRECTORY is correct;
        // the transport must resolve the real file by project dir + matching cwd, not by
        // the payload filename, and tolerate the differing on-disk session id.
        var projectDir = Path.Combine(_root, "projects", "encoded-cwd");
        Directory.CreateDirectory(projectDir);
        var runCwd = Path.Combine(_root, "real worktree").Replace('\\', '/');
        // The REAL transcript: a DIFFERENT session id in its filename and content, a
        // matching cwd, assistant text + usage + an assistant end_turn.
        var realTranscript = Path.Combine(projectDir, "ad940d31-real.jsonl");
        File.WriteAllText(realTranscript, string.Join('\n',
            TranscriptLine("on-disk-session", runCwd, "assistant", model: "claude-haiku-4-5",
                text: "WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"recovered\\\",\\\"files_changed\\\":[],\\\"failure_reason\\\":null}",
                stopReason: "end_turn", usage: true)));
        // The payload points at a session-id filename that DOES NOT EXIST in that dir.
        var bogusPayloadPath = Path.Combine(projectDir, "d91740c9-does-not-exist.jsonl");

        var waiter = new FakeWaiter((run, _) => Task.FromResult(new ClaudeCompletionRecord(
            ClaudeCompletionStore.CurrentSchemaVersion, run.RunId, "d91740c9-claimed", runCwd,
            bogusPayloadPath, "final message only", false, DateTimeOffset.UtcNow)));

        var result = await ExecuteAsync(new FakeLauncher(new FakeProcess()), waiter);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("recovered", result.Summary);
        // Telemetry is recovered from the resolved transcript even though the payload
        // filename pointed at a non-existent session.
        Assert.True(result.Metadata.ContainsKey("llm_usage"));
        var usage = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Metadata["llm_usage"]);
        Assert.Equal("claude-haiku-4-5", usage["model"]);
        Assert.Equal(11, usage["input_tokens"]);
        Assert.Equal(22, usage["output_tokens"]);
    }

    // Builds a single transcript JSONL line by concatenation so the trailing JSON
    // braces never collide with interpolation. text is already JSON-escaped.
    private static string TranscriptLine(
        string sessionId, string cwd, string type, string model, string text, string stopReason, bool usage)
    {
        var usageJson = usage
            ? ",\"usage\":{\"input_tokens\":11,\"output_tokens\":22,\"cache_read_input_tokens\":33,\"cache_creation_input_tokens\":44}"
            : "";
        return "{\"type\":\"" + type + "\",\"sessionId\":" + Json(sessionId) + ",\"cwd\":" + Json(cwd) +
            ",\"message\":{\"model\":\"" + model + "\",\"id\":\"m1\",\"role\":\"assistant\",\"stop_reason\":\"" + stopReason +
            "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]" + usageJson + "}}";
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);

    [Fact]
    public void InteractiveArgs_PreservePermissionsToolsModelAndNeverPrint()
    {
        var args = ClaudeCodeInteractiveTransport.BuildInteractiveArgs(
            new ClaudeCodeOptions { BypassPermissions = true, ExtraArgs = ["--append-system-prompt", "extra"] },
            new WorkerOptions(TimeSpan.FromMinutes(1), AllowedTools: ["Read", "Grep"], LeanPlanning: true),
            "C:/run/settings.json",
            "claude-sonnet-4-6");

        Assert.DoesNotContain("--print", args);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("Read,Grep", args);
        Assert.Contains("TodoWrite,Task", args);
        Assert.Contains("claude-sonnet-4-6", args);
        Assert.Equal("Read .build/brief.md, execute it completely, and obey the brief's final-output contract.", args[^1]);
    }

    [Fact]
    public void SettingsBuilder_CommandPrefixQuotesDotnetAndAssemblySeparately()
    {
        var json = ClaudeHookSettingsBuilder.Build(
            ["C:/Program Files/dotnet/dotnet.exe", "C:/repo with space/build.dll"], "C:/run path/id", "id");
        using var document = JsonDocument.Parse(json);
        var command = document.RootElement.GetProperty("hooks").GetProperty("Stop")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString();

        Assert.StartsWith("'C:/Program Files/dotnet/dotnet.exe' 'C:/repo with space/build.dll' internal", command);
    }

    [Fact]
    public async Task SameWorktreeCollision_IsPreventedWithoutLaunchingClaude()
    {
        var worktree = Path.Combine(_root, "shared worktree");
        Directory.CreateDirectory(worktree);
        // Simulate another live interactive run already holding the worktree lock.
        using var held = new FileStream(
            InteractiveClaudeWorktreeLock.PathFor(worktree), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var launcher = new FakeLauncher(new FakeProcess());
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("nope"))));

        var result = await ExecuteAsync(launcher, waiter, worktree: worktree);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("active in this worktree", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(launcher.Spec); // Claude was never launched
    }

    [Fact]
    public async Task IndependentWorktrees_RunConcurrentlyWithDistinctRuns()
    {
        var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Task<WorkerResult> Run()
        {
            var waiter = new FakeWaiter((run, _) =>
            {
                observed.Add(run.RunId);
                return Task.FromResult(Completion(run.RunId, WorkerResultText("ok")));
            });
            return ExecuteAsync(new FakeLauncher(new FakeProcess()), waiter);
        }

        var results = await Task.WhenAll(Run(), Run());

        Assert.All(results, r => Assert.Equal(Status.Ok, r.Status));
        Assert.Equal(2, new HashSet<string>(observed).Count); // two distinct private run ids
    }

    [Fact]
    public async Task WorktreeWithSpacesAndUnicode_WritesBriefAndCompletes()
    {
        // Escapes keep this source ASCII while exercising a spaced + Unicode path.
        var worktree = Path.Combine(_root, "work tree \u00e9\u4f60\u597d");
        var launcher = new FakeLauncher(new FakeProcess());
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("unicode ok"))));

        var result = await ExecuteAsync(launcher, waiter, worktree: worktree);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("unicode ok", result.Summary);
        // The spawn cwd is canonicalized to claude's own form (symlinks resolved) so
        // the turn-detector match works on macOS where the temp path is a symlink.
        Assert.Equal(ClaudeRealPath.Resolve(worktree), launcher.Spec!.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(worktree, ".build", "brief.md")));
    }

    [Fact]
    public async Task ExecutableMissingDuringLaunch_ReportsActionableFailure()
    {
        var launcher = new FakeLauncher(new FakeProcess(),
            new System.ComponentModel.Win32Exception("The system cannot find the file specified"));
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("unreached"))));

        var result = await ExecuteAsync(launcher, waiter);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("Worker executable not found", result.Summary);
        Assert.Contains("Run directory", result.FailureReason);
    }

    [Fact]
    public async Task CancellationCompletionRace_StaysDeterministicUnderRepetition()
    {
        for (var i = 0; i < 200; i++)
        {
            var process = new FakeProcess();
            var completion = new TaskCompletionSource<ClaudeCompletionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new FakeWaiter((_, _) => completion.Task);
            using var cancellation = new CancellationTokenSource();

            var task = ExecuteAsync(new FakeLauncher(process), waiter, cancellationToken: cancellation.Token);
            var run = await waiter.RunObserved.Task;

            // Trusted completion and cancellation fire as close together as possible.
            completion.SetResult(Completion(run.RunId, WorkerResultText("race")));
            cancellation.Cancel();

            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

            // A completion that resolved successfully always wins the race.
            Assert.True(result.Status == Status.Ok, $"iteration {i}: {result.Status} {result.Summary}: {result.FailureReason}");
            Assert.True(process.Killed);
            Assert.True(process.Disposed);
            Assert.False(Directory.Exists(run.Path));
        }
    }

    [Fact]
    public async Task TurnDone_SendsExitThenCompletionResolvesToOk()
    {
        var process = new FakeProcess();
        var turnSignal = new FakeTurnSignal();
        // Completion only resolves once /exit has been observed on the process input,
        // modelling the Stop hook firing on interactive session exit.
        var completion = new TaskCompletionSource<ClaudeCompletionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new FakeWaiter((run, _) => completion.Task);

        var task = ExecuteAsync(new FakeLauncher(process), waiter, turnSignal: turnSignal);
        var run = await waiter.RunObserved.Task;
        await turnSignal.WorkingDirectoryObserved.Task;

        // No /exit until the turn-done signal fires.
        Assert.Empty(process.Inputs);
        turnSignal.SignalTurnDone();

        // The transport sends /exit; only then do we resolve the completion.
        await WaitUntilAsync(() => process.Inputs.Count > 0);
        Assert.Contains("/exit\r", process.Inputs);
        completion.SetResult(Completion(run.RunId, WorkerResultText("exited then completed")));

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("exited then completed", result.Summary);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task CompletionBeforeTurnDone_SucceedsWithoutSendingExit()
    {
        var process = new FakeProcess();
        // Turn signal never fires; the per-turn hook completion resolves immediately.
        var turnSignal = new FakeTurnSignal();
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("hook fired per-turn"))));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter, turnSignal: turnSignal);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("hook fired per-turn", result.Summary);
        // The completion path won, so no /exit was needed.
        Assert.Empty(process.Inputs);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task TurnDone_WriteInputFailure_StillCompletesViaCompletion()
    {
        // Writing /exit throws, but the completion still resolves (best-effort write).
        var process = new FakeProcess(writeException: new IOException("pipe closed"));
        var turnSignal = new FakeTurnSignal();
        var completion = new TaskCompletionSource<ClaudeCompletionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new FakeWaiter((run, _) => completion.Task);

        var task = ExecuteAsync(new FakeLauncher(process), waiter, turnSignal: turnSignal);
        var run = await waiter.RunObserved.Task;
        await turnSignal.WorkingDirectoryObserved.Task;
        turnSignal.SignalTurnDone();

        await WaitUntilAsync(() => process.Inputs.Count > 0);
        completion.SetResult(Completion(run.RunId, WorkerResultText("survived write failure")));

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("survived write failure", result.Summary);
    }

    [Fact]
    public async Task TurnDone_NoCompletionAfterExit_TimesOutWithActionableFailure()
    {
        var process = new FakeProcess();
        var turnSignal = new FakeTurnSignal();
        var waiter = NeverCompletes();

        var task = ExecuteAsync(new FakeLauncher(process), waiter,
            timeout: TimeSpan.FromMilliseconds(200), turnSignal: turnSignal);
        await turnSignal.WorkingDirectoryObserved.Task;
        turnSignal.SignalTurnDone();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("/exit\r", process.Inputs);
        Assert.Contains("did not complete after exit", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after the interactive session exited", result.FailureReason);
        Assert.True(process.Killed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(5);
        }
    }

    private FakeWaiter NeverCompletes() => new((_, cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ContinueWith<ClaudeCompletionRecord>(_ => throw new UnreachableException(), CancellationToken.None));

    private async Task<WorkerResult> ExecuteAsync(
        FakeLauncher launcher,
        FakeWaiter waiter,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        string? debugDirectory = null,
        TextWriter? progressSink = null,
        string? worktree = null,
        IInteractiveTurnSignal? turnSignal = null)
    {
        worktree ??= Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var options = new ClaudeCodeOptions { Transport = ClaudeCodeTransport.InteractiveHook };
        // Default to a turn signal that never fires so the existing tests exercise the
        // completion-first path (a per-turn hook firing) exactly as before.
        var transport = new ClaudeCodeInteractiveTransport(
            options, launcher, waiter, turnSignal ?? new NeverTurnSignal(), ["build.exe"]);
        return await transport.ExecuteAsync(
            new Brief("TLB-live", Phase.Implement, "test brief", [], [], new Dictionary<string, string>()),
            worktree,
            new WorkerOptions(timeout ?? TimeSpan.FromSeconds(5), DebugCaptureDirectory: debugDirectory,
                ProgressDigestSink: progressSink),
            cancellationToken);
    }

    private static ClaudeCompletionRecord Completion(
        string runId,
        string response,
        string transcriptPath = "C:/transcript",
        string sessionId = "session") =>
        new(ClaudeCompletionStore.CurrentSchemaVersion, runId, sessionId, "C:/repo", transcriptPath, response, false, DateTimeOffset.UtcNow);

    private string CopyFixture(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), path);
        return path;
    }

    private static string WorkerResultText(string summary) => $$"""
        WORKER_RESULT
        {"status":"Ok","summary":"{{summary}}","files_changed":[],"failure_reason":null}
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeLauncher(FakeProcess process, Exception? launchException = null) : IInteractiveClaudeProcessLauncher
    {
        public InteractiveClaudeLaunchSpec? Spec { get; private set; }

        public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
        {
            Spec = spec;
            if (launchException is not null) throw launchException;
            return process;
        }
    }

    private sealed class FakeWaiter(
        Func<ClaudeRunDirectory, CancellationToken, Task<ClaudeCompletionRecord>> wait) : IClaudeCompletionWaiter
    {
        public ClaudeRunDirectory? Run { get; private set; }
        public TaskCompletionSource<ClaudeRunDirectory> RunObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClaudeCompletionRecord> WaitAsync(ClaudeRunDirectory run, CancellationToken cancellationToken)
        {
            Run = run;
            RunObserved.TrySetResult(run);
            return wait(run, cancellationToken);
        }
    }

    private sealed class FakeProcess : IInteractiveClaudeProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? _killException;
        private readonly Exception? _writeException;

        public FakeProcess(int? exitCode = null, Exception? killException = null, Exception? writeException = null)
        {
            _killException = killException;
            _writeException = writeException;
            if (exitCode is int code) _exit.SetResult(code);
        }

        public Task<int> ExitTask => _exit.Task;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> Inputs { get; } = [];

        public Task WriteInputAsync(string text, CancellationToken cancellationToken)
        {
            Inputs.Add(text);
            if (_writeException is not null) throw _writeException;
            return Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Killed = true;
            if (_killException is not null) throw _killException;
            _exit.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // Default turn signal for the existing suite: never reports a turn, so those tests
    // exercise the completion-first path (a per-turn Stop hook firing) unchanged.
    private sealed class NeverTurnSignal : IInteractiveTurnSignal
    {
        public Task WaitForTurnAsync(string workingDirectory, DateTimeOffset launchedAt, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public string DescribeCandidates(string workingDirectory, DateTimeOffset launchedAt) =>
            $"fake_never_turn_signal worktree={workingDirectory}\n";
    }

    // Completes WaitForTurnAsync on demand so a test can drive the /exit flow.
    private sealed class FakeTurnSignal : IInteractiveTurnSignal
    {
        private readonly TaskCompletionSource _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> WorkingDirectoryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalTurnDone() => _turn.TrySetResult();

        public Task WaitForTurnAsync(string workingDirectory, DateTimeOffset launchedAt, CancellationToken cancellationToken)
        {
            WorkingDirectoryObserved.TrySetResult(workingDirectory);
            return _turn.Task.WaitAsync(cancellationToken);
        }

        public string DescribeCandidates(string workingDirectory, DateTimeOffset launchedAt) =>
            $"fake_turn_signal worktree={workingDirectory}\n";
    }
}

public sealed class ClaudeInteractiveLiveFactAttribute : FactAttribute
{
    public ClaudeInteractiveLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LATTICEFLOW_RUN_CLAUDE_INTERACTIVE_LIVE") != "1")
            Skip = "Set LATTICEFLOW_RUN_CLAUDE_INTERACTIVE_LIVE=1 to consume Claude usage.";
    }
}

public sealed class ClaudeCodeInteractiveLiveTests
{
    [ClaudeInteractiveLiveFact]
    public async Task LiveSentinel_UsesInteractiveHookWithoutPrint()
    {
        var repositoryRoot = FindRepositoryRoot();
        var cliProject = Path.Combine(repositoryRoot, "src", "ThroughlineBuild.Cli", "ThroughlineBuild.Cli.csproj");
        await RunAsync("dotnet", ["build", cliProject, "--nologo", "-v", "q"], repositoryRoot);
        var cliAssembly = Path.Combine(repositoryRoot, "src", "ThroughlineBuild.Cli", "bin", "Debug", "net10.0", "build.dll");

        var worktree = Path.Combine(Path.GetTempPath(), $"lattice claude live {Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        try
        {
            await RunAsync("git", ["init", "-q"], worktree);
            var options = new ClaudeCodeOptions
            {
                Transport = ClaudeCodeTransport.InteractiveHook,
                Sizes = new Dictionary<WorkerSize, ModelTier> { [WorkerSize.Small] = new("haiku") }
            };
            var brief = new Brief("TLB-live", Phase.Implement, """
                Do not modify files. Finish with exactly this result envelope:
                WORKER_RESULT
                {"status":"Ok","summary":"INTERACTIVE_HOOK_SENTINEL","files_changed":[],"failure_reason":null}
                """, [], [], new Dictionary<string, string>());

            var printOptions = new ClaudeCodeOptions
            {
                Transport = ClaudeCodeTransport.Print,
                Sizes = new Dictionary<WorkerSize, ModelTier> { [WorkerSize.Small] = new("haiku") }
            };
            var printResult = await new ClaudeCodePrintTransport(printOptions, new ClaudeCodeProgressDigester())
                .ExecuteAsync(brief, worktree, new WorkerOptions(TimeSpan.FromMinutes(3), Size: WorkerSize.Small),
                    CancellationToken.None);

            // Use the production platform factory so this exercises the real host
            // (ConPTY on Windows, the PTY host on Unix), not a hardcoded Windows host.
            var launcher = new CapturingLauncher(InteractiveClaudeProcessLauncherFactory.Create());
            var transport = new ClaudeCodeInteractiveTransport(
                options, launcher, new ClaudeCompletionWaiter(),
                new TranscriptTurnSignal(),
                ["dotnet", cliAssembly]);
            // Capture outside the worktree so a failed run's transcript survives the
            // finally cleanup and stays diagnosable.
            var debugDirectory = Path.Combine(Path.GetTempPath(), $"lattice-interactive-debug-{Guid.NewGuid():N}");
            Console.WriteLine($"interactive debug capture: {debugDirectory}");
            var result = await transport.ExecuteAsync(
                brief,
                worktree,
                new WorkerOptions(TimeSpan.FromMinutes(3), Size: WorkerSize.Small,
                    DebugCaptureDirectory: debugDirectory),
                CancellationToken.None);

            Assert.True(printResult.Status == Status.Ok,
                $"print: {printResult.Summary}: {printResult.FailureReason}");
            Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
            Assert.Equal("INTERACTIVE_HOOK_SENTINEL", result.Summary);
            Assert.DoesNotContain("--print", launcher.Spec!.Arguments);
            Assert.True(launcher.Process!.ExitTask.IsCompleted);
            Assert.True(result.Metadata.ContainsKey("llm_usage"));
            Assert.True(result.Metadata.ContainsKey("context_turns"));
            var observedToolCall = File.ReadLines(Path.Combine(debugDirectory, "transcript.jsonl"))
                .Select(line => JsonDocument.Parse(line))
                .Any(document => document.RootElement.TryGetProperty("tool_count", out var count)
                    && count.GetInt32() > 0);
            Assert.True(observedToolCall);

            var printUsage = (IReadOnlyDictionary<string, object>)printResult.Metadata["llm_usage"];
            var interactiveUsage = (IReadOnlyDictionary<string, object?>)result.Metadata["llm_usage"];
            Console.WriteLine($"print model={printUsage["model"]} input={printUsage["input_tokens"]} " +
                $"output={printUsage["output_tokens"]} cache_read={printUsage["cache_read_tokens"]} cache_create={printUsage["cache_create_tokens"]}");
            Console.WriteLine($"interactive model={interactiveUsage["model"]} input={interactiveUsage["input_tokens"]} " +
                $"output={interactiveUsage["output_tokens"]} cache_read={interactiveUsage["cache_read_tokens"]} cache_create={interactiveUsage["cache_create_tokens"]}");
        }
        finally
        {
            if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true);
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "throughline-build.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class CapturingLauncher(IInteractiveClaudeProcessLauncher inner) : IInteractiveClaudeProcessLauncher
    {
        public InteractiveClaudeLaunchSpec? Spec { get; private set; }
        public IInteractiveClaudeProcess? Process { get; private set; }

        public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
        {
            Spec = spec;
            Process = inner.Launch(spec);
            return Process;
        }
    }
}
