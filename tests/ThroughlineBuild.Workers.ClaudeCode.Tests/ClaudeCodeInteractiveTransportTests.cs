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
        var process = new FakeProcess();
        var waiter = new FakeWaiter((run, _) => Task.FromResult(Completion(run.RunId, WorkerResultText("debug"))));

        var result = await ExecuteAsync(new FakeLauncher(process), waiter, debugDirectory: debugDirectory);

        Assert.True(result.Status == Status.Ok, $"{result.Summary}: {result.FailureReason}");
        Assert.True(Directory.Exists(waiter.Run!.Path));
        Assert.True(File.Exists(Path.Combine(waiter.Run.Path, "settings.json")));
    }

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

    private FakeWaiter NeverCompletes() => new((_, cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ContinueWith<ClaudeCompletionRecord>(_ => throw new UnreachableException(), CancellationToken.None));

    private async Task<WorkerResult> ExecuteAsync(
        FakeLauncher launcher,
        FakeWaiter waiter,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        string? debugDirectory = null)
    {
        var worktree = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var options = new ClaudeCodeOptions { Transport = ClaudeCodeTransport.InteractiveHook };
        var transport = new ClaudeCodeInteractiveTransport(options, launcher, waiter, ["build.exe"]);
        return await transport.ExecuteAsync(
            new Brief("TLB-live", Phase.Implement, "test brief", [], [], new Dictionary<string, string>()),
            worktree,
            new WorkerOptions(timeout ?? TimeSpan.FromSeconds(5), DebugCaptureDirectory: debugDirectory),
            cancellationToken);
    }

    private static ClaudeCompletionRecord Completion(string runId, string response) =>
        new(ClaudeCompletionStore.CurrentSchemaVersion, runId, "session", "C:/repo", "C:/transcript", response, false, DateTimeOffset.UtcNow);

    private static string WorkerResultText(string summary) => $$"""
        WORKER_RESULT
        {"status":"Ok","summary":"{{summary}}","files_changed":[],"failure_reason":null}
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeLauncher(FakeProcess process) : IInteractiveClaudeProcessLauncher
    {
        public InteractiveClaudeLaunchSpec? Spec { get; private set; }

        public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
        {
            Spec = spec;
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

        public FakeProcess(int? exitCode = null, Exception? killException = null)
        {
            _killException = killException;
            if (exitCode is int code) _exit.SetResult(code);
        }

        public Task<int> ExitTask => _exit.Task;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public Task KillTreeAsync(CancellationToken cancellationToken)
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
            var launcher = new CapturingLauncher(new WindowsConPtyClaudeProcessLauncher());
            var transport = new ClaudeCodeInteractiveTransport(
                options, launcher, new ClaudeCompletionWaiter(),
                ["dotnet", cliAssembly]);
            var result = await transport.ExecuteAsync(
                new Brief("TLB-live", Phase.Implement, """
                    Do not modify files. Finish with exactly this result envelope:
                    WORKER_RESULT
                    {"status":"Ok","summary":"INTERACTIVE_HOOK_SENTINEL","files_changed":[],"failure_reason":null}
                    """, [], [], new Dictionary<string, string>()),
                worktree,
                new WorkerOptions(TimeSpan.FromMinutes(3), Size: WorkerSize.Small),
                CancellationToken.None);

            Assert.Equal(Status.Ok, result.Status);
            Assert.Equal("INTERACTIVE_HOOK_SENTINEL", result.Summary);
            Assert.DoesNotContain("--print", launcher.Spec!.Arguments);
            Assert.True(launcher.Process!.ExitTask.IsCompleted);
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
