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
        // The payload points at a session-id filename that DOES NOT EXIST in that dir.
        var bogusPayloadPath = Path.Combine(projectDir, "d91740c9-does-not-exist.jsonl");

        var waiter = new FakeWaiter((run, _) =>
        {
            // claude persists the run nonce (the run id) in the transcript's user prompt,
            // and the legacy re-resolve now requires it, so write the real transcript here
            // where run.RunId is known. The locator must resolve by cwd + nonce, not the
            // bogus payload filename, and tolerate the differing on-disk session id.
            File.WriteAllText(realTranscript, string.Join('\n',
                UserLineWithNonce(runCwd, run.RunId),
                TranscriptLine("on-disk-session", runCwd, "assistant", model: "claude-haiku-4-5",
                    text: "WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"recovered\\\",\\\"files_changed\\\":[],\\\"failure_reason\\\":null}",
                    stopReason: "end_turn", usage: true)));
            return Task.FromResult(new ClaudeCompletionRecord(
                ClaudeCompletionStore.CurrentSchemaVersion, run.RunId, "d91740c9-claimed", runCwd,
                bogusPayloadPath, "final message only", false, DateTimeOffset.UtcNow));
        });

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

    // A user-prompt line embedding the run nonce exactly as the transport writes it, so
    // the legacy re-resolve's nonce match exercises reality. cwd lets it also satisfy the
    // cwd match.
    private static string UserLineWithNonce(string cwd, string nonce) =>
        "{\"type\":\"user\",\"cwd\":" + Json(cwd) +
        ",\"message\":{\"role\":\"user\",\"content\":" +
        Json("Read .build/brief.md (throughline-build run token, ignore: " + nonce + ")") + "}}";

    [Fact]
    public async Task TurnDone_ConsumesExactLocatedTranscript_IgnoringNewerSameCwdTranscript()
    {
        // Finding 2: once turn detection has located THIS run's transcript, the synthesized
        // completion must consume that EXACT file - not re-resolve by newest-same-cwd, which
        // a concurrent MCP/session write could replace between turn-detect and parse.
        var worktree = Path.Combine(_root, "exact worktree");
        Directory.CreateDirectory(worktree);
        var projectDir = Path.Combine(_root, "projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        var cwd = ClaudeRealPath.Resolve(worktree).Replace('\\', '/');

        string Transcript(string summary) =>
            TranscriptLine("on-disk-session", cwd, "assistant", "claude-haiku-4-5",
                "WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"" + summary +
                "\\\",\\\"files_changed\\\":[],\\\"failure_reason\\\":null}",
                stopReason: "end_turn", usage: true);

        // The transcript turn detection located (what the transport must consume)...
        var located = Path.Combine(projectDir, "located.jsonl");
        File.WriteAllText(located, Transcript("from located transcript"));
        // ...and a NEWER same-cwd transcript a competing writer dropped in the same dir.
        var newer = Path.Combine(projectDir, "newer-competitor.jsonl");
        File.WriteAllText(newer, Transcript("from newer competitor"));
        File.SetLastWriteTimeUtc(located, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(5));

        var process = new FakeProcess();
        var turnSignal = new FakeTurnSignal();
        var waiter = NeverCompletes(); // completion.json never written - synthesis only

        var task = ExecuteAsync(new FakeLauncher(process), waiter, worktree: worktree, turnSignal: turnSignal);
        await turnSignal.WorkingDirectoryObserved.Task;
        turnSignal.SignalTurnDone(located);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        // The EXACT located transcript wins even though a newer same-cwd file exists; a
        // re-resolve would have returned "from newer competitor".
        Assert.Equal("from located transcript", result.Summary);
        Assert.False(Directory.Exists(waiter.Run!.Path));
    }

    [Fact]
    public async Task MissingResult_NudgesLiveSessionThenSucceedsOnNextTurn()
    {
        // Reprompt backstop: turn 1 ends without a WORKER_RESULT (a conversational yield);
        // the transport nudges the LIVE session, waits for the NEXT turn, and turn 2 emits
        // the envelope -> Ok. (Sub-agent forking is already disabled; this catches the
        // "the agent is running, I'll report back" yield.)
        var turn1 = WriteTranscript("Implementation agent launched; I will report back with the commit SHA.", envelope: false);
        var turn2 = WriteTranscript("nudged-pass", envelope: true);
        var process = new FakeProcess();
        var turnSignal = new SequencedTurnSignal(turn1, turn2);

        var result = await ExecuteAsync(new FakeLauncher(process), NeverCompletes(), turnSignal: turnSignal)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("nudged-pass", result.Summary);
        // The re-armed wait asked for the NEXT turn (minEndTurns 1 then 2)...
        Assert.Equal(new[] { 1, 2 }, turnSignal.RequestedMinEndTurns);
        // ...and the live session got exactly one nudge, then the graceful /exit.
        Assert.Equal(2, process.Inputs.Count);
        Assert.Contains("WORKER_RESULT block", process.Inputs[0]);
        Assert.Equal("/exit\r", process.Inputs[^1]);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task MissingResult_NudgeBudgetExhausted_FailsWithoutLooping()
    {
        // The backstop is bounded: if the worker still yields no WORKER_RESULT after the
        // single nudge, the transport finalizes and fails - it never loops forever.
        var turn1 = WriteTranscript("Still working; I will report back.", envelope: false);
        var turn2 = WriteTranscript("Agent still running; reporting back soon.", envelope: false);
        var process = new FakeProcess();
        var turnSignal = new SequencedTurnSignal(turn1, turn2);

        var result = await ExecuteAsync(new FakeLauncher(process), NeverCompletes(), turnSignal: turnSignal)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("No WORKER_RESULT", result.Summary);
        Assert.Equal(new[] { 1, 2 }, turnSignal.RequestedMinEndTurns);
        // Exactly one nudge then /exit - not an unbounded retry.
        Assert.Equal(2, process.Inputs.Count);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task EnvelopeOnFirstTurn_DoesNotNudge()
    {
        // No regression: a turn that already carries a WORKER_RESULT is finalized directly,
        // with only the graceful /exit and no re-armed wait.
        var turn1 = WriteTranscript("first-turn-pass", envelope: true);
        var process = new FakeProcess();
        var turnSignal = new SequencedTurnSignal(turn1);

        var result = await ExecuteAsync(new FakeLauncher(process), NeverCompletes(), turnSignal: turnSignal)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("first-turn-pass", result.Summary);
        Assert.Equal(new[] { 1 }, turnSignal.RequestedMinEndTurns);
        Assert.Equal(new[] { "/exit\r" }, process.Inputs);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task BypassPermissions_DoesNotInjectIsSandboxIntoTheEnvironment()
    {
        // With --dangerously-skip-permissions, claude's PTY host presents a one-time "Bypass
        // Permissions mode" acceptance dialog the flag does not auto-accept. The transport
        // suppresses it with the narrowly-scoped skipDangerousModePermissionPrompt key in the
        // ephemeral settings.json (see SettingsBuilder_EmitsSkipDangerousModePromptWhenRequested),
        // NOT IS_SANDBOX - which would falsely flip claude's GLOBAL sandbox state and can alter
        // tool/permission behavior. BypassPermissions defaults to true.
        var launcher = new FakeLauncher(new FakeProcess());
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("ok"))));

        await ExecuteAsync(launcher, waiter);

        Assert.False(launcher.Spec!.Environment.ContainsKey("IS_SANDBOX"));
    }

    [Fact]
    public void InteractiveArgs_PreservePermissionsToolsModelAndNeverPrint()
    {
        var args = ClaudeCodeInteractiveTransport.BuildInteractiveArgs(
            new ClaudeCodeOptions { BypassPermissions = true, ExtraArgs = ["--append-system-prompt", "extra"] },
            new WorkerOptions(TimeSpan.FromMinutes(1), AllowedTools: ["Read", "Grep"], LeanPlanning: true),
            "C:/run/settings.json",
            "claude-sonnet-4-6",
            "deadbeefcafef00d1234567890abcdef");

        Assert.DoesNotContain("--print", args);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("Read,Grep", args);
        Assert.Contains("Agent,Task,TodoWrite", args);
        Assert.Contains("claude-sonnet-4-6", args);
        // The prompt is the last arg, opens with the base instruction, and carries the run
        // nonce verbatim so the transcript locator can correlate this run's transcript.
        Assert.StartsWith("Read .build/brief.md, execute it completely, and obey the brief's final-output contract.", args[^1]);
        Assert.Contains("deadbeefcafef00d1234567890abcdef", args[^1]);
    }

    [Fact]
    public void InteractiveArgs_NonLeanTicket_StillDisallowsSubagentTools()
    {
        // The big-ticket regression: a one-shot worker must never be able to spawn a nested
        // sub-agent and yield ("the agent is running, I'll report back") - which leaves the
        // Stop-hook completion with no WORKER_RESULT. The sub-agent disallow is unconditional,
        // not gated on lean planning (lean only fires for S-effort tickets).
        var args = ClaudeCodeInteractiveTransport.BuildInteractiveArgs(
            new ClaudeCodeOptions { BypassPermissions = true },
            new WorkerOptions(TimeSpan.FromMinutes(1)),
            "C:/run/settings.json",
            "claude-sonnet-4-6",
            "deadbeefcafef00d1234567890abcdef").ToList();

        var i = args.IndexOf("--disallowedTools");
        Assert.True(i >= 0, "expected --disallowedTools in argv even for non-lean tickets");
        Assert.Equal("Agent,Task", args[i + 1]);
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
    public void SettingsBuilder_EmitsSkipDangerousModePromptWhenRequested()
    {
        // The dialog-suppression key is present (and true) only when requested, and omitted
        // entirely otherwise so it never leaks into runs that do not pass the bypass flag.
        var without = ClaudeHookSettingsBuilder.Build(["dotnet", "build.dll"], "C:/run/id", "id");
        Assert.DoesNotContain("skipDangerousModePermissionPrompt", without);

        var with = ClaudeHookSettingsBuilder.Build(
            ["dotnet", "build.dll"], "C:/run/id", "id", skipDangerousModePermissionPrompt: true);
        using var document = JsonDocument.Parse(with);
        Assert.True(document.RootElement.GetProperty("skipDangerousModePermissionPrompt").GetBoolean());
    }

    [Fact]
    public void SettingsBuilder_CanOmitStopHookForLibraryHosts()
    {
        var json = ClaudeHookSettingsBuilder.BuildWithoutStopHook(skipDangerousModePermissionPrompt: true);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("skipDangerousModePermissionPrompt").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("hooks").EnumerateObject());
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
    public async Task TurnDone_SynthesizesCompletionFromTranscriptWithoutCompletionJson()
    {
        // The Stop hook never fires (the cross-platform reality): no completion.json is
        // ever written. The turn-detector locates the run's transcript and the transport
        // synthesizes the completion from it, recovering the WORKER_RESULT and telemetry.
        var worktree = Path.Combine(_root, "synth worktree");
        Directory.CreateDirectory(worktree);
        var transcriptPath = WriteRunTranscript(worktree, "claude-haiku-4-5", "synthesized from transcript");

        var process = new FakeProcess();
        var turnSignal = new FakeTurnSignal();
        // The completion waiter NEVER resolves - proving synthesis does not depend on it.
        var waiter = NeverCompletes();

        var task = ExecuteAsync(new FakeLauncher(process), waiter, worktree: worktree, turnSignal: turnSignal);
        await turnSignal.WorkingDirectoryObserved.Task;

        // No /exit until the turn-done signal fires.
        Assert.Empty(process.Inputs);
        turnSignal.SignalTurnDone(transcriptPath);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("synthesized from transcript", result.Summary);
        // /exit is still sent as a best-effort graceful nudge before termination.
        Assert.Contains("/exit\r", process.Inputs);
        Assert.True(process.Killed);
        // Telemetry is recovered from the located transcript, with no completion.json.
        Assert.True(result.Metadata.ContainsKey("llm_usage"));
        var usage = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Metadata["llm_usage"]);
        Assert.Equal("claude-haiku-4-5", usage["model"]);
        Assert.Equal(11, usage["input_tokens"]);
        Assert.Equal(22, usage["output_tokens"]);
        Assert.False(Directory.Exists(waiter.Run!.Path));
    }

    [Fact]
    public async Task CompletionBeforeTurnDone_UsesStopHookFastPathWithoutSendingExit()
    {
        var process = new FakeProcess();
        // Turn signal never fires; a real Stop-hook completion.json resolves first
        // (backward-compatible fast-path).
        var turnSignal = new FakeTurnSignal();
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("hook fired"))));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter, turnSignal: turnSignal);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("hook fired", result.Summary);
        // The completion fast-path won, so no /exit was needed.
        Assert.Empty(process.Inputs);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task TurnDone_WriteInputFailure_StillSynthesizesCompletion()
    {
        // Writing /exit throws, but synthesis from the located transcript still
        // succeeds (the /exit nudge is best-effort and never load-bearing).
        var worktree = Path.Combine(_root, "write fail worktree");
        Directory.CreateDirectory(worktree);
        var transcriptPath = WriteRunTranscript(worktree, "claude-haiku-4-5", "survived write failure");

        var process = new FakeProcess(writeException: new IOException("pipe closed"));
        var turnSignal = new FakeTurnSignal();
        var waiter = NeverCompletes();

        var task = ExecuteAsync(new FakeLauncher(process), waiter, worktree: worktree, turnSignal: turnSignal);
        await turnSignal.WorkingDirectoryObserved.Task;
        turnSignal.SignalTurnDone(transcriptPath);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.Equal("survived write failure", result.Summary);
        Assert.True(process.Killed);
    }

    // Writes a single-line persisted transcript whose cwd matches the run's canonical
    // worktree (so ParseCompletion's locator re-resolves it) carrying the WORKER_RESULT
    // envelope, an assistant end_turn, and usage. Returns the transcript path.
    private string WriteRunTranscript(string worktree, string model, string summary)
    {
        var projectDir = Path.Combine(_root, "projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDir);
        var cwd = ClaudeRealPath.Resolve(worktree).Replace('\\', '/');
        var path = Path.Combine(projectDir, Guid.NewGuid().ToString("N") + ".jsonl");
        var text = "WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"" + summary +
            "\\\",\\\"files_changed\\\":[],\\\"failure_reason\\\":null}";
        File.WriteAllText(path, TranscriptLine("on-disk-session", cwd, "assistant", model,
            text, stopReason: "end_turn", usage: true));
        return path;
    }

    [Fact]
    public async Task ExecuteAsync_PreflightUnsupported_FailsClearlyWithoutLaunching()
    {
        // The interactive transport runs its capability preflight before any side effect: an
        // unsupported host fails clearly and the process is never launched (no fallback to print).
        // This is the chokepoint that guards every worker-spawning path, not just the phase verbs.
        var launcher = new FakeLauncher(new FakeProcess(exitCode: 0));
        var options = new ClaudeCodeOptions { Transport = ClaudeCodeTransport.InteractiveHook, ExecutablePath = "claude" };
        var failingPreflight = new Func<string, CancellationToken, Task<ClaudePreflightResult>>((_, _) =>
            Task.FromResult(new ClaudePreflightResult(false, ClaudePreflightFailureKind.VersionTooOld,
                "transport = \"interactive-hook\" requires Claude Code >= 2.1.177, but the installed claude is 2.1.150.",
                new Version(2, 1, 150), "2.1.150")));
        var transport = new ClaudeCodeInteractiveTransport(
            options, launcher, NeverCompletes(), new NeverTurnSignal(), ["build.exe"], failingPreflight);

        var worktree = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var result = await transport.ExecuteAsync(
            new Brief("TLB-pf", Phase.Implement, "brief", [], [], new Dictionary<string, string>()),
            worktree,
            new WorkerOptions(TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("Interactive Claude transport unavailable", result.Summary);
        Assert.Contains("2.1.177", result.FailureReason);
        Assert.Null(launcher.Spec); // Launch was never called
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

    // Writes a one-line persisted transcript (a single assistant end_turn message) under
    // _root and returns its path, for driving the synthesize/nudge flow. envelope=true
    // embeds a WORKER_RESULT whose summary is assistantText; envelope=false uses
    // assistantText as plain assistant prose with no envelope.
    private string WriteTranscript(string assistantText, bool envelope)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        var text = envelope
            ? "WORKER_RESULT\\n{\\\"status\\\":\\\"Ok\\\",\\\"summary\\\":\\\"" + assistantText +
              "\\\",\\\"files_changed\\\":[],\\\"failure_reason\\\":null}"
            : assistantText;
        File.WriteAllText(path, TranscriptLine("on-disk-session", "C:/repo", "assistant", "claude-haiku-4-5",
            text, stopReason: "end_turn", usage: false));
        return path;
    }

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
    // exercise the completion-first fast-path (a per-turn Stop hook firing) unchanged.
    private sealed class NeverTurnSignal : IInteractiveTurnSignal
    {
        public Task<string?> WaitForTurnAsync(string workingDirectory, string runNonce, DateTimeOffset launchedAt, CancellationToken cancellationToken, int minEndTurns = 1) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith<string?>(_ => null, CancellationToken.None);

        public string DescribeCandidates(string workingDirectory, string runNonce, DateTimeOffset launchedAt) =>
            $"fake_never_turn_signal worktree={workingDirectory}\n";
    }

    // Resolves WaitForTurnAsync on demand with a located transcript path so a test can
    // drive the synthesize-from-transcript flow.
    private sealed class FakeTurnSignal : IInteractiveTurnSignal
    {
        private readonly TaskCompletionSource<string?> _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> WorkingDirectoryObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The run nonce the transport passed in (for tests that assert correlation).
        public string? ObservedNonce { get; private set; }

        // Resolves the turn with the transcript path the transport will synthesize from.
        public void SignalTurnDone(string transcriptPath) => _turn.TrySetResult(transcriptPath);

        public Task<string?> WaitForTurnAsync(string workingDirectory, string runNonce, DateTimeOffset launchedAt, CancellationToken cancellationToken, int minEndTurns = 1)
        {
            ObservedNonce = runNonce;
            WorkingDirectoryObserved.TrySetResult(workingDirectory);
            return _turn.Task.WaitAsync(cancellationToken);
        }

        public string DescribeCandidates(string workingDirectory, string runNonce, DateTimeOffset launchedAt) =>
            $"fake_turn_signal worktree={workingDirectory}\n";
    }

    // Hands out one located-transcript path per WaitForTurnAsync call (turn 1, turn 2,
    // ...), recording the minEndTurns the transport requested each time so a test can
    // assert the re-arm waits for the NEXT turn. Once the queue drains it blocks until
    // cancelled, so the transport finalizes or times out rather than spinning.
    private sealed class SequencedTurnSignal : IInteractiveTurnSignal
    {
        private readonly Queue<string> _paths;
        public List<int> RequestedMinEndTurns { get; } = [];

        public SequencedTurnSignal(params string[] paths) => _paths = new Queue<string>(paths);

        public Task<string?> WaitForTurnAsync(string workingDirectory, string runNonce, DateTimeOffset launchedAt, CancellationToken cancellationToken, int minEndTurns = 1)
        {
            RequestedMinEndTurns.Add(minEndTurns);
            return _paths.Count > 0
                ? Task.FromResult<string?>(_paths.Dequeue())
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith<string?>(_ => null, CancellationToken.None);
        }

        public string DescribeCandidates(string workingDirectory, string runNonce, DateTimeOffset launchedAt) =>
            $"sequenced_turn_signal worktree={workingDirectory}\n";
    }
}

public sealed class ClaudeInteractiveLiveFactAttribute : FactAttribute
{
    public ClaudeInteractiveLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THROUGHLINE_BUILD_RUN_CLAUDE_INTERACTIVE_LIVE") != "1")
            Skip = "Set THROUGHLINE_BUILD_RUN_CLAUDE_INTERACTIVE_LIVE=1 to consume Claude usage.";
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

        const string sentinelMarker = "SENTINEL_MARKER_8F3A";
        // Isolation is part of the correctness contract, not cleanup: the print baseline
        // and the interactive run get SEPARATE worktrees. Sharing one worktree let the
        // print baseline's persisted transcript (same cwd + end_turn) stand in for the
        // interactive run's during turn detection, so the "interactive" path could pass on
        // the PRINT transcript. With distinct worktrees the interactive turn-detector can
        // only match the interactive run's own transcript - the test is now honest about
        // whether interactive Claude actually completed.
        var printWorktree = await CreateSeededWorktreeAsync(sentinelMarker);
        var interactiveWorktree = await CreateSeededWorktreeAsync(sentinelMarker);
        try
        {
            var options = new ClaudeCodeOptions
            {
                Transport = ClaudeCodeTransport.InteractiveHook,
                Sizes = new Dictionary<WorkerSize, ModelTier> { [WorkerSize.Small] = new("haiku") }
            };
            // Strong imperative + verbatim envelope so haiku stays deterministic: it must
            // read the seeded file with the Read tool (capturing a tool_use), then output
            // ONLY the envelope. Do not modify, do not narrate.
            var brief = new Brief("TLB-live", Phase.Implement, $$"""
                Use the Read tool to read the file sentinel-input.txt in the current
                directory and confirm it contains the marker {{sentinelMarker}}.
                Do not modify any files. Do not write any explanation or narration.
                After reading the file, output EXACTLY the following result envelope and
                nothing else - no preamble, no commentary, no code fences:
                WORKER_RESULT
                {"status":"Ok","summary":"INTERACTIVE_HOOK_SENTINEL","files_changed":[],"failure_reason":null}
                """, [], [], new Dictionary<string, string>());

            var printOptions = new ClaudeCodeOptions
            {
                Transport = ClaudeCodeTransport.Print,
                Sizes = new Dictionary<WorkerSize, ModelTier> { [WorkerSize.Small] = new("haiku") }
            };
            var printResult = await new ClaudeCodePrintTransport(printOptions, new ClaudeCodeProgressDigester())
                .ExecuteAsync(brief, printWorktree, new WorkerOptions(TimeSpan.FromMinutes(3), Size: WorkerSize.Small),
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
                interactiveWorktree,
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
            foreach (var worktree in new[] { printWorktree, interactiveWorktree })
                if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true);
        }
    }

    // Creates a fresh git-init'd worktree seeded with sentinel-input.txt so the brief can
    // induce a DETERMINISTIC Read tool call (a captured tool_use makes observedToolCall
    // meaningful). Each transport gets its own so neither rides the other's transcript.
    private static async Task<string> CreateSeededWorktreeAsync(string sentinelMarker)
    {
        var worktree = Path.Combine(Path.GetTempPath(), $"lattice claude live {Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        await RunAsync("git", ["init", "-q"], worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, "sentinel-input.txt"), sentinelMarker + "\n");
        return worktree;
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
