using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ThroughlineBuild.Workers.ClaudeCode;

internal sealed class WindowsConPtyClaudeProcessLauncher : IInteractiveClaudeProcessLauncher
{
    public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The Windows ConPTY host requires Windows; print remains available on other hosts.");
        return WindowsConPtyClaudeProcess.Start(spec);
    }
}

/// <summary>
/// Hosts interactive Claude inside a Windows pseudo console (ConPTY). The whole
/// child tree is placed in a kill-on-close job object so no descendant - including
/// tool subprocesses Claude spawns - can survive cleanup, and shutdown escalates
/// from closing the console (graceful) to terminating the job (forced).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConPtyClaudeProcess : IInteractiveClaudeProcess
{
    private static readonly TimeSpan DefaultGracefulTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultForcedTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly SafeFileHandle _input;
    private readonly FileStream _output;
    private readonly Task _drainTask;
    private readonly TimeSpan _gracefulTimeout;
    private readonly TimeSpan _forcedTimeout;
    private readonly object _gate = new();
    private IntPtr _pseudoConsole;
    private IntPtr _job;

    private WindowsConPtyClaudeProcess(
        Process process,
        IntPtr pseudoConsole,
        IntPtr job,
        SafeFileHandle input,
        FileStream output,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout)
    {
        _process = process;
        _pseudoConsole = pseudoConsole;
        _job = job;
        _input = input;
        _output = output;
        _gracefulTimeout = gracefulTimeout;
        _forcedTimeout = forcedTimeout;
        _drainTask = output.CopyToAsync(Stream.Null);
        ExitTask = WaitForExitAsync(process);
    }

    public Task<int> ExitTask { get; }

    /// <summary>Root process id of the launched tree; exposed for tree-cleanup tests.</summary>
    internal int ProcessId => _process.Id;

    public static WindowsConPtyClaudeProcess Start(InteractiveClaudeLaunchSpec spec) =>
        Start(spec, DefaultGracefulTimeout, DefaultForcedTimeout);

    public static WindowsConPtyClaudeProcess Start(
        InteractiveClaudeLaunchSpec spec,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout)
    {
        IntPtr inputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr job = IntPtr.Zero;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        var processInfo = default(ProcessInformation);
        var started = false;
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
                attributes, 0, (IntPtr)PseudoConsoleAttribute, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            // The job is the tree-containment guarantee: every descendant inherits
            // job membership and the kernel kills the whole job when it is closed.
            job = TryCreateKillOnCloseJob();

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startup.AttributeList = attributes;
            var commandLine = new StringBuilder(RenderCommandLine(spec.Executable, spec.Arguments));
            environment = Marshal.StringToHGlobalUni(RenderEnvironment(spec.Environment));
            ThrowIfFalse(NativeMethods.CreateProcess(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended,
                environment, spec.WorkingDirectory, ref startup, out processInfo));
            started = true;

            // Assign while still suspended so no grandchild escapes the job, then resume.
            if (job != IntPtr.Zero && !NativeMethods.AssignProcessToJobObject(job, processInfo.Process))
            {
                NativeMethods.CloseHandle(job);
                job = IntPtr.Zero; // fall back to entire-tree kill if assignment is refused
            }
            if (NativeMethods.ResumeThread(processInfo.Thread) == unchecked((uint)-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var pid = (int)processInfo.ProcessId;
            NativeMethods.CloseHandle(processInfo.Thread);
            processInfo.Thread = IntPtr.Zero;
            var process = Process.GetProcessById(pid);
            NativeMethods.CloseHandle(processInfo.Process);
            processInfo.Process = IntPtr.Zero;

            var output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            var result = new WindowsConPtyClaudeProcess(
                process, pseudoConsole, job, inputWrite, output, gracefulTimeout, forcedTimeout);
            // Ownership has moved to the returned host; stop the catch/finally from freeing it.
            pseudoConsole = IntPtr.Zero;
            job = IntPtr.Zero;
            inputWrite = null;
            outputRead = null;
            return result;
        }
        catch
        {
            if (started)
            {
                if (job != IntPtr.Zero) NativeMethods.TerminateJobObject(job, 1);
                if (processInfo.Process != IntPtr.Zero)
                {
                    NativeMethods.TerminateProcess(processInfo.Process, 1);
                    NativeMethods.CloseHandle(processInfo.Process);
                }
                if (processInfo.Thread != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.Thread);
            }
            inputWrite?.Dispose();
            outputRead?.Dispose();
            if (job != IntPtr.Zero) NativeMethods.CloseHandle(job);
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

    public Task TerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TerminateCoreAsync();
    }

    private async Task TerminateCoreAsync()
    {
        var error = await ProcessShutdownSequence.RunAsync(
            ExitTask,
            () => _process.HasExited,
            () => { ClosePseudoConsoleOnce(); return Task.CompletedTask; },
            () => { ForceKillTree(); return Task.CompletedTask; },
            _gracefulTimeout,
            _forcedTimeout).ConfigureAwait(false);
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    public async ValueTask DisposeAsync()
    {
        _input.Dispose();
        ClosePseudoConsoleOnce();
        // Closing a kill-on-close job is the final safety net: any descendant that
        // somehow outlived termination is reaped here.
        CloseJobOnce();
        try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        _output.Dispose();
        _process.Dispose();
    }

    private void ForceKillTree()
    {
        if (_job != IntPtr.Zero)
        {
            try { NativeMethods.TerminateJobObject(_job, 1); } catch { }
        }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private void ClosePseudoConsoleOnce()
    {
        lock (_gate)
        {
            if (_pseudoConsole == IntPtr.Zero) return;
            var handle = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
            NativeMethods.ClosePseudoConsole(handle);
        }
    }

    private void CloseJobOnce()
    {
        lock (_gate)
        {
            if (_job == IntPtr.Zero) return;
            var handle = _job;
            _job = IntPtr.Zero;
            NativeMethods.CloseHandle(handle);
        }
    }

    private static IntPtr TryCreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var length = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!NativeMethods.SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, length))
        {
            NativeMethods.CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    private static async Task<int> WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
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

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int PseudoConsoleAttribute = 0x00020016;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
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

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);
    }
}
