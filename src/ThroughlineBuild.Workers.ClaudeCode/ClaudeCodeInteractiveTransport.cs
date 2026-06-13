using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal sealed record InteractiveClaudeLaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

internal interface IInteractiveClaudeProcess : IAsyncDisposable
{
    Task<int> ExitTask { get; }
    Task KillTreeAsync(CancellationToken cancellationToken);
}

internal interface IInteractiveClaudeProcessLauncher
{
    IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec);
}

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

    private readonly ClaudeCodeOptions _options;
    private readonly IInteractiveClaudeProcessLauncher _launcher;
    private readonly IClaudeCompletionWaiter _completionWaiter;
    private readonly IReadOnlyList<string>? _hookCommandPrefix;

    internal ClaudeCodeInteractiveTransport(ClaudeCodeOptions options)
        : this(options, new WindowsConPtyClaudeProcessLauncher(), new ClaudeCompletionWaiter()) { }

    internal ClaudeCodeInteractiveTransport(
        ClaudeCodeOptions options,
        IInteractiveClaudeProcessLauncher launcher,
        IClaudeCompletionWaiter completionWaiter,
        IReadOnlyList<string>? hookCommandPrefix = null)
    {
        _options = options;
        _launcher = launcher;
        _completionWaiter = completionWaiter;
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
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, "brief.md"), brief.Instruction, ct);

        var runId = Guid.NewGuid().ToString("N");
        var preserveRun = options.DebugCaptureDirectory is not null;
        var runParent = preserveRun
            ? Path.Combine(options.DebugCaptureDirectory!, "claude-interactive-runs")
            : Path.Combine(Path.GetTempPath(), "latticeflow-claude-runs");
        var runPath = Path.Combine(runParent, runId);
        Directory.CreateDirectory(runPath);
        var run = ClaudeRunDirectory.Open(runPath, runId);

        IInteractiveClaudeProcess? process = null;
        try
        {
            var settingsPath = Path.Combine(run.Path, "settings.json");
            await File.WriteAllTextAsync(
                settingsPath,
                ClaudeHookSettingsBuilder.Build(_hookCommandPrefix ?? ResolveHookCommandPrefix(), run.Path, run.RunId),
                ct);

            _options.Sizes.TryGetValue(options.Size, out var tier);
            var model = ClaudeCodeAgent.NormalizeModel(tier?.Model);
            if (ClaudeCodeModelValidator.Validate(model) is string modelError)
                return Failure("Invalid Claude Code model", $"{modelError}. Run directory: '{run.Path}'.");

            var arguments = BuildInteractiveArgs(_options, options, settingsPath, model);
            var environmentInfo = new ProcessStartInfo(_options.ExecutablePath) { UseShellExecute = false };
            ClaudeCodeAgent.ConfigureEnvironment(environmentInfo, _options, options);
            var environment = environmentInfo.Environment.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            try
            {
                process = _launcher.Launch(new InteractiveClaudeLaunchSpec(
                    _options.ExecutablePath, arguments, workingDirectory, environment));
            }
            catch (Win32Exception ex)
            {
                return Failure(
                    $"Worker executable not found: '{_options.ExecutablePath}'",
                    $"Unable to launch interactive Claude Code with ConPTY: {ex.Message}. Run directory: '{run.Path}'.");
            }
            catch (PlatformNotSupportedException ex)
            {
                return Failure("Interactive Claude Code is unsupported on this host",
                    $"{ex.Message} Run directory: '{run.Path}'.");
            }

            using var waitCancellation = new CancellationTokenSource();
            var completionTask = _completionWaiter.WaitAsync(run, waitCancellation.Token);
            var timeoutTask = Task.Delay(options.Timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var winner = await Task.WhenAny(completionTask, process.ExitTask, timeoutTask, cancellationTask);

            if (completionTask.IsCompletedSuccessfully)
            {
                var completion = await completionTask;
                var killFailure = await TryKillAndWaitAsync(process);
                if (killFailure is not null)
                    return Failure("Interactive Claude process cleanup failed",
                        $"{killFailure}Run directory: '{run.Path}'.");
                if (!string.Equals(completion.RunId, run.RunId, StringComparison.Ordinal))
                    return Failure("Claude Stop-hook completion was stale",
                        $"Completion run id '{completion.RunId}' did not match expected run id '{run.RunId}'. Run directory: '{run.Path}'.");
                return ParseCompletion(completion, run.Path);
            }

            waitCancellation.Cancel();
            if (winner == cancellationTask)
            {
                var killFailure = await TryKillAndWaitAsync(process);
                return Failure("Interactive Claude execution was cancelled",
                    $"Cancellation requested. {killFailure}Run directory: '{run.Path}'.");
            }

            if (winner == timeoutTask)
            {
                var killFailure = await TryKillAndWaitAsync(process);
                return Failure("Interactive Claude execution timed out",
                    $"Timed out after {options.Timeout}. {killFailure}Run directory: '{run.Path}'.");
            }

            if (winner == completionTask)
            {
                try { await completionTask; }
                catch (Exception ex)
                {
                    var killFailure = await TryKillAndWaitAsync(process);
                    return Failure("Claude Stop-hook completion was malformed",
                        $"{ex.Message}. {killFailure}Run directory: '{run.Path}'.");
                }
            }

            var exitCode = await process.ExitTask;
            return Failure("Interactive Claude exited before trusted completion",
                $"Claude exited with code {exitCode} before the correlated Stop hook completed. Run directory: '{run.Path}'.");
        }
        finally
        {
            if (process is not null)
                await process.DisposeAsync();
            if (!preserveRun)
                TryDeleteRunDirectory(run.Path);
        }
    }

    internal static IReadOnlyList<string> BuildInteractiveArgs(
        ClaudeCodeOptions claudeOptions,
        WorkerOptions workerOptions,
        string settingsPath,
        string? model)
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
        arguments.AddRange(["--settings", settingsPath, InitialPrompt]);
        return arguments;
    }

    private static WorkerResult ParseCompletion(ClaudeCompletionRecord completion, string runPath)
    {
        if (ClaudeCodeAgent.TryDescribeInvalidModelError(completion.LastAssistantMessage, "") is string invalidModel)
            return Failure("Claude Code rejected the configured model", $"{invalidModel} Run directory: '{runPath}'.");

        var outcome = WorkerResultParser.TryParse(completion.LastAssistantMessage);
        if (outcome.Result is not null)
            return outcome.Result with { Blocks = outcome.Blocks };

        if (outcome.DeserializeErrorType is not null)
            return Failure("Failed to deserialize WORKER_RESULT JSON",
                $"{outcome.DeserializeErrorType}: {outcome.DeserializeErrorMessage}. Run directory: '{runPath}'.");

        return Failure("No WORKER_RESULT found in interactive Claude completion",
            $"The trusted Stop-hook completion did not contain a WORKER_RESULT block. Run directory: '{runPath}'.");
    }

    private static async Task<string?> TryKillAndWaitAsync(IInteractiveClaudeProcess process)
    {
        try
        {
            await process.KillTreeAsync(CancellationToken.None);
            await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
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

    private static void TryDeleteRunDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}

internal sealed class WindowsConPtyClaudeProcessLauncher : IInteractiveClaudeProcessLauncher
{
    public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The interactive-hook transport currently requires Windows ConPTY; print remains available on other hosts.");
        return WindowsConPtyClaudeProcess.Start(spec);
    }
}

internal sealed class WindowsConPtyClaudeProcess : IInteractiveClaudeProcess
{
    private readonly Process _process;
    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _input;
    private readonly FileStream _output;
    private readonly Task _drainTask;

    private WindowsConPtyClaudeProcess(
        Process process,
        IntPtr pseudoConsole,
        SafeFileHandle input,
        FileStream output)
    {
        _process = process;
        _pseudoConsole = pseudoConsole;
        _input = input;
        _output = output;
        _drainTask = output.CopyToAsync(Stream.Null);
        ExitTask = WaitForExitAsync(process);
    }

    public Task<int> ExitTask { get; }

    public static WindowsConPtyClaudeProcess Start(InteractiveClaudeLaunchSpec spec)
    {
        IntPtr inputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        try
        {
            ThrowIfFalse(NativeMethods.CreatePipe(out inputRead, out var inputWriteRaw, IntPtr.Zero, 0));
            inputWrite = new SafeFileHandle(inputWriteRaw, ownsHandle: true);
            ThrowIfFalse(NativeMethods.CreatePipe(out var outputReadRaw, out outputWrite, IntPtr.Zero, 0));
            outputRead = new SafeFileHandle(outputReadRaw, ownsHandle: true);

            var hr = NativeMethods.CreatePseudoConsole(new Coord(120, 40), inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            NativeMethods.CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            NativeMethods.CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            nuint attributeSize = 0;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            attributes = Marshal.AllocHGlobal((nint)attributeSize);
            ThrowIfFalse(NativeMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize));
            ThrowIfFalse(NativeMethods.UpdateProcThreadAttribute(
                attributes, 0, (IntPtr)0x00020016, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startup.AttributeList = attributes;
            var commandLine = new StringBuilder(RenderCommandLine(spec.Executable, spec.Arguments));
            environment = Marshal.StringToHGlobalUni(RenderEnvironment(spec.Environment));
            ThrowIfFalse(NativeMethods.CreateProcess(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | 0x00000400 | 0x08000000,
                environment, spec.WorkingDirectory, ref startup, out var processInfo));

            NativeMethods.CloseHandle(processInfo.Thread);
            NativeMethods.CloseHandle(processInfo.Process);
            var process = Process.GetProcessById((int)processInfo.ProcessId);
            return new WindowsConPtyClaudeProcess(
                process, pseudoConsole, inputWrite, new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false));
        }
        catch
        {
            inputWrite?.Dispose();
            outputRead?.Dispose();
            if (pseudoConsole != IntPtr.Zero) NativeMethods.ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally
        {
            if (inputRead != IntPtr.Zero) NativeMethods.CloseHandle(inputRead);
            if (outputWrite != IntPtr.Zero) NativeMethods.CloseHandle(outputWrite);
            if (attributes != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public Task KillTreeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _input.Dispose();
        if (_pseudoConsole != IntPtr.Zero) NativeMethods.ClosePseudoConsole(_pseudoConsole);
        try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _output.Dispose();
        _process.Dispose();
    }

    private static async Task<int> WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static string RenderCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"')) return value;
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') builder.Append('\\', slashes * 2 + 1).Append(ch);
            else { builder.Append('\\', slashes).Append(ch); }
            slashes = 0;
        }
        builder.Append('\\', slashes * 2).Append('"');
        return builder.ToString();
    }

    private static string RenderEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        builder.Append('\0');
        return builder.ToString();
    }

    private static void ThrowIfFalse(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord(short x, short y)
    {
        public short X = x;
        public short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        internal static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);
    }
}
