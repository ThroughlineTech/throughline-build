using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Unix arm of the interactive terminal host abstraction. Allocates a pseudo
/// terminal so Claude detects an interactive tty, then launches it with
/// <c>posix_spawnp</c> in a new session (its own process group) so the whole tree
/// can be reaped with a process-group signal. This is the platform-specific drop-in
/// behind the same <see cref="IInteractiveClaudeProcess"/> the transport uses on
/// Windows; the transport never silently falls back to <c>--print</c>.
/// </summary>
internal sealed class UnixInteractiveClaudeProcessLauncher : IInteractiveClaudeProcessLauncher
{
    public IInteractiveClaudeProcess Launch(InteractiveClaudeLaunchSpec spec)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Unix terminal host is not available on Windows.");
        return UnixPtyClaudeProcess.Start(spec);
    }
}

/// <summary>
/// Pure argument helpers for the Unix host, kept platform-neutral so they are
/// testable on every host.
/// </summary>
internal static class UnixSpawnArguments
{
    public static string[] RenderEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var result = new List<string>(environment.Count);
        foreach (var pair in environment)
        {
            if (pair.Value is null) continue;
            result.Add($"{pair.Key}={pair.Value}");
        }
        return [.. result];
    }
}

/// <summary>
/// Hosts interactive Claude on Unix inside a pseudo terminal. The child is spawned
/// as a session leader (<c>POSIX_SPAWN_SETSID</c>), so its process-group id equals
/// its pid and tool subprocesses it spawns inherit the group. Shutdown escalates
/// from <c>SIGTERM</c> to <c>SIGKILL</c> on the whole group, then verifies the
/// group has actually drained.
///
/// Containment is process-GROUP based, not a kernel job object: this is weaker than
/// the Windows ConPTY job object. A descendant that deliberately leaves the group
/// (its own <c>setsid</c>/<c>setpgid</c>, or a double-fork daemon) escapes
/// <c>kill(-pid, ...)</c>. Well-behaved tool subprocesses stay in the group and are
/// reaped. There is also a microsecond pid-reuse window inherent to every
/// process-group cleanup: between the leader being reaped and a group signal, the
/// group id could in principle be reused; this is the standard limitation of
/// <c>killpg</c>-based teardown.
///
/// Requires glibc &gt;= 2.26 (POSIX_SPAWN_SETSID) / &gt;= 2.29 (file-action chdir) or
/// macOS &gt;= 10.15 - satisfied by the linux-x64 and osx-arm64 CI targets. No
/// controlling terminal is bound (the slave is opened O_NOCTTY and posix_spawn
/// cannot run TIOCSCTTY); <c>isatty()</c> is still true on the dup'd fds, which is
/// all Claude's interactive detection needs, and teardown uses signals to the group
/// rather than terminal-generated Ctrl-C.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixPtyClaudeProcess : IInteractiveClaudeProcess
{
    private static readonly TimeSpan DefaultGracefulTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultForcedTimeout = TimeSpan.FromSeconds(10);

    private readonly int _pid;
    private readonly FileStream _master;
    private readonly Task _drainTask;
    private readonly TimeSpan _gracefulTimeout;
    private readonly TimeSpan _forcedTimeout;
    private readonly object _gate = new();

    private UnixPtyClaudeProcess(int pid, SafeFileHandle master, TimeSpan gracefulTimeout, TimeSpan forcedTimeout)
    {
        _pid = pid;
        _master = new FileStream(master, FileAccess.Read);
        _gracefulTimeout = gracefulTimeout;
        _forcedTimeout = forcedTimeout;
        _drainTask = _master.CopyToAsync(Stream.Null);
        ExitTask = ObserveExitAsync(pid);
    }

    public Task<int> ExitTask { get; }

    public static UnixPtyClaudeProcess Start(InteractiveClaudeLaunchSpec spec) =>
        Start(spec, DefaultGracefulTimeout, DefaultForcedTimeout);

    public static UnixPtyClaudeProcess Start(
        InteractiveClaudeLaunchSpec spec,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout)
    {
        var master = OpenPseudoTerminal(out var slavePath);
        var slave = -1;
        try
        {
            slave = NativeMethods.open(slavePath, ONoctty | ORdwr);
            if (slave < 0) throw LastError("open(pts slave)");

            var pid = SpawnChild(spec, master, slave);

            // The child holds its own dup of the slave; the parent does not need it.
            NativeMethods.close(slave);
            slave = -1;

            var handle = new SafeFileHandle((IntPtr)master, ownsHandle: true);
            master = -1;
            return new UnixPtyClaudeProcess(pid, handle, gracefulTimeout, forcedTimeout);
        }
        catch
        {
            if (slave >= 0) NativeMethods.close(slave);
            if (master >= 0) NativeMethods.close(master);
            throw;
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
            () => ExitTask.IsCompleted,
            () => { SignalGroup(Sigterm); return Task.CompletedTask; },
            () => { SignalGroup(Sigkill); return Task.CompletedTask; },
            _gracefulTimeout,
            _forcedTimeout).ConfigureAwait(false);

        // The leader is gone; the contract is the whole tree. Flush and confirm the
        // process group has actually drained (tool subprocesses, not just the leader).
        var groupError = await EnsureGroupDrainedAsync().ConfigureAwait(false);

        if (error is not null) throw new InvalidOperationException(error);
        if (groupError is not null) throw new InvalidOperationException(groupError);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        // The transport skips TerminateAsync when the leader exits before a trusted
        // completion and relies on disposal alone, so disposal must run the same
        // bounded group-drain (SIGKILL + kill(-pid,0) confirmation) rather than a
        // single fire-and-forget signal. Best-effort: disposal never throws.
        try { await EnsureGroupDrainedAsync().ConfigureAwait(false); } catch { }
        _master.Dispose();
        try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
    }

    ~UnixPtyClaudeProcess()
    {
        // Backstop for an abandoned instance: kill the group so the observer's
        // blocking waitpid returns and reaps the leader instead of leaking a thread.
        try { SignalGroup(Sigkill); } catch { }
    }

    private async Task<string?> EnsureGroupDrainedAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _forcedTimeout)
        {
            if (!GroupAlive()) return null;
            SignalGroup(Sigkill);
            await Task.Delay(20).ConfigureAwait(false);
        }
        return GroupAlive()
            ? "the interactive process group did not fully terminate after SIGKILL"
            : null;
    }

    private bool GroupAlive()
    {
        lock (_gate)
        {
            // sig 0 probes existence. ESRCH => the group is gone; anything else
            // (including EPERM) is treated as still present.
            if (NativeMethods.kill(-_pid, 0) == 0) return true;
            return Marshal.GetLastPInvokeError() != Esrch;
        }
    }

    private void SignalGroup(int signal)
    {
        lock (_gate)
        {
            // Negative pid targets the whole process group (pgid == pid via setsid).
            NativeMethods.kill(-_pid, signal);
        }
    }

    private Task<int> ObserveExitAsync(int pid)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int rc, status;
            do { rc = NativeMethods.waitpid(pid, out status, 0); }
            while (rc < 0 && Marshal.GetLastPInvokeError() == Eintr);
            completion.TrySetResult(rc < 0 ? -1 : DecodeExitCode(status));
        })
        { IsBackground = true, Name = $"claude-pty-wait-{pid}" };
        thread.Start();
        return completion.Task;
    }

    private static int DecodeExitCode(int status)
    {
        var signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }

    private static int OpenPseudoTerminal(out string slavePath)
    {
        var master = NativeMethods.posix_openpt(ORdwr);
        if (master < 0) throw LastError("posix_openpt");
        try
        {
            if (NativeMethods.grantpt(master) != 0) throw LastError("grantpt");
            if (NativeMethods.unlockpt(master) != 0) throw LastError("unlockpt");
            slavePath = SlaveName(master);
            return master;
        }
        catch
        {
            NativeMethods.close(master);
            throw;
        }
    }

    private static string SlaveName(int master)
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS has no ptsname_r; ptsname returns a pointer to a static buffer.
            var ptr = NativeMethods.ptsname(master);
            if (ptr == IntPtr.Zero) throw LastError("ptsname");
            return Marshal.PtrToStringUTF8(ptr) ?? throw new InvalidOperationException("ptsname returned an unreadable name.");
        }

        var buffer = new byte[256];
        var rc = NativeMethods.ptsname_r(master, buffer, (nuint)buffer.Length);
        if (rc != 0) throw new Win32Exception(rc, "ptsname_r failed.");
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static int SpawnChild(InteractiveClaudeLaunchSpec spec, int master, int slave)
    {
        var fileActions = AllocZeroed(SpawnStructSize);
        var attributes = AllocZeroed(SpawnStructSize);
        var argv = MarshalStringArray([spec.Executable, .. spec.Arguments]);
        var envp = MarshalStringArray(UnixSpawnArguments.RenderEnvironment(spec.Environment));
        try
        {
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_init(fileActions), "file_actions_init");
            // Run in the requested worktree so the child resolves .build/brief.md.
            if (!string.IsNullOrEmpty(spec.WorkingDirectory))
                ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_addchdir_np(fileActions, spec.WorkingDirectory), "addchdir_np");
            // Give the child the pty slave on stdin/stdout/stderr; drop both the
            // master and the original slave fd so only fds 0/1/2 reference the pty
            // (a stray slave fd would defeat EOF/HUP on the master).
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 0), "adddup2(0)");
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 1), "adddup2(1)");
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 2), "adddup2(2)");
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_addclose(fileActions, slave), "addclose(slave)");
            ThrowOnSpawnError(NativeMethods.posix_spawn_file_actions_addclose(fileActions, master), "addclose(master)");

            ThrowOnSpawnError(NativeMethods.posix_spawnattr_init(attributes), "attr_init");
            // POSIX_SPAWN_SETSID requires glibc >= 2.26 / macOS >= 10.15.
            ThrowOnSpawnError(NativeMethods.posix_spawnattr_setflags(attributes, PosixSpawnSetsid),
                "attr_setflags(POSIX_SPAWN_SETSID; requires glibc>=2.26 / macOS>=10.15)");

            var rc = NativeMethods.posix_spawnp(out var pid, spec.Executable, fileActions, attributes, argv, envp);
            if (rc == Enoent)
                throw new Win32Exception(rc, $"Worker executable not found on PATH: '{spec.Executable}'.");
            if (rc != 0)
                throw new Win32Exception(rc, $"posix_spawnp failed for '{spec.Executable}'.");
            return pid;
        }
        finally
        {
            NativeMethods.posix_spawn_file_actions_destroy(fileActions);
            NativeMethods.posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attributes);
            FreeStringArray(argv);
            FreeStringArray(envp);
        }
    }

    private static IntPtr AllocZeroed(int size)
    {
        var block = Marshal.AllocHGlobal(size);
        // glibc/macOS spawn structs must start zeroed before *_init populates them.
        Marshal.Copy(new byte[size], 0, block, size);
        return block;
    }

    private static IntPtr MarshalStringArray(IReadOnlyList<string> values)
    {
        var block = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
        for (var i = 0; i < values.Count; i++)
            Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
        Marshal.WriteIntPtr(block, values.Count * IntPtr.Size, IntPtr.Zero);
        return block;
    }

    private static void FreeStringArray(IntPtr block)
    {
        for (var offset = 0; ; offset += IntPtr.Size)
        {
            var ptr = Marshal.ReadIntPtr(block, offset);
            if (ptr == IntPtr.Zero) break;
            Marshal.FreeCoTaskMem(ptr);
        }
        Marshal.FreeHGlobal(block);
    }

    private static void ThrowOnSpawnError(int rc, string what)
    {
        if (rc != 0) throw new Win32Exception(rc, $"{what} failed.");
    }

    private static Win32Exception LastError(string what)
    {
        var errno = Marshal.GetLastPInvokeError();
        return new Win32Exception(errno, $"{what} failed (errno {errno}).");
    }

    // O_RDWR is 2 on Linux and macOS; O_NOCTTY differs.
    private const int ORdwr = 0x0002;
    private static int ONoctty => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
    // POSIX_SPAWN_SETSID: 0x80 on glibc, 0x0400 on macOS.
    private static short PosixSpawnSetsid => (short)(OperatingSystem.IsMacOS() ? 0x0400 : 0x80);
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Eintr = 4;
    private const int Enoent = 2;
    private const int Esrch = 3;
    // glibc posix_spawnattr_t (~336 bytes) is the larger of the two opaque structs;
    // a 512-byte zeroed buffer comfortably holds either platform's representation.
    private const int SpawnStructSize = 512;

    private static class NativeMethods
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int posix_openpt(int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int grantpt(int fd);

        [DllImport("libc", SetLastError = true)]
        internal static extern int unlockpt(int fd);

        [DllImport("libc", SetLastError = true)]
        internal static extern int ptsname_r(int fd, byte[] buf, nuint buflen);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr ptsname(int fd);

        [DllImport("libc", SetLastError = true)]
        internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        internal static extern int kill(int pid, int sig);

        [DllImport("libc", SetLastError = true)]
        internal static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc", SetLastError = true)]
        internal static extern int posix_spawnp(
            out int pid,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
            IntPtr fileActions,
            IntPtr attrp,
            IntPtr argv,
            IntPtr envp);

        [DllImport("libc")]
        internal static extern int posix_spawn_file_actions_init(IntPtr fileActions);

        [DllImport("libc")]
        internal static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

        [DllImport("libc")]
        internal static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int fd, int newFd);

        [DllImport("libc")]
        internal static extern int posix_spawn_file_actions_addclose(IntPtr fileActions, int fd);

        [DllImport("libc")]
        internal static extern int posix_spawn_file_actions_addchdir_np(
            IntPtr fileActions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport("libc")]
        internal static extern int posix_spawnattr_init(IntPtr attr);

        [DllImport("libc")]
        internal static extern int posix_spawnattr_destroy(IntPtr attr);

        [DllImport("libc")]
        internal static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    }
}
