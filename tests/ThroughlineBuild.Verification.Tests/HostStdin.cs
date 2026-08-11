using System.Runtime.InteropServices;

namespace ThroughlineBuild.Verification.Tests;

/// <summary>
/// Repoints this process's standard input at a file for the life of the returned scope, then puts
/// the original back. Used to prove that a spawned check does not inherit the caller's stdin - the
/// only way to assert that from inside the test host, since what a child inherits is decided by the
/// OS-level handle rather than by anything reachable through <see cref="Console"/>.
/// </summary>
/// <remarks>
/// Operates below <see cref="Console"/> deliberately: Console.SetIn swaps a managed TextReader and
/// has no effect on what a child process inherits. Scoped to a using block and restored in Dispose.
/// The redirected handle is left inheritable on purpose - an inheritable file handle is the case
/// that leaks visibly, which is what makes the assertion able to fail.
/// </remarks>
internal sealed class HostStdin : IDisposable
{
    private const int StdInputHandle = -10;
    private const int HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const int OReadOnly = 0; // O_RDONLY is 0 on Linux and macOS alike

    private readonly IntPtr _windowsOriginal;
    private readonly IntPtr _windowsOpened;
    private readonly int _unixSavedFd = -1;
    private readonly int _unixOpenedFd = -1;
    private bool _disposed;

    private HostStdin(IntPtr windowsOriginal, IntPtr windowsOpened)
    {
        _windowsOriginal = windowsOriginal;
        _windowsOpened = windowsOpened;
    }

    private HostStdin(int unixSavedFd, int unixOpenedFd)
    {
        _unixSavedFd = unixSavedFd;
        _unixOpenedFd = unixOpenedFd;
    }

    public static IDisposable RedirectToFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var original = GetStdHandle(StdInputHandle);
            var opened = CreateFileW(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (opened == new IntPtr(-1))
                throw new IOException($"could not open {path} as stdin: win32 {Marshal.GetLastWin32Error()}");

            // A non-inheritable handle would be its own (different) leak; make it inheritable so the
            // test measures the redirect and not handle-inheritance rules.
            if (!SetHandleInformation(opened, HandleFlagInherit, HandleFlagInherit))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(opened);
                throw new IOException($"could not make stdin handle inheritable: win32 {error}");
            }
            if (!SetStdHandle(StdInputHandle, opened))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(opened);
                throw new IOException($"could not set stdin handle: win32 {error}");
            }

            return new HostStdin(original, opened);
        }

        var saved = dup(0);
        if (saved < 0)
            throw new IOException($"could not dup stdin: errno {Marshal.GetLastWin32Error()}");

        var fd = open(path, OReadOnly);
        if (fd < 0)
        {
            close(saved);
            throw new IOException($"could not open {path} as stdin: errno {Marshal.GetLastWin32Error()}");
        }

        if (dup2(fd, 0) < 0)
        {
            close(fd);
            close(saved);
            throw new IOException($"could not redirect stdin: errno {Marshal.GetLastWin32Error()}");
        }

        return new HostStdin(saved, fd);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (OperatingSystem.IsWindows())
        {
            if (!SetStdHandle(StdInputHandle, _windowsOriginal))
            {
                throw new IOException(
                    $"could not restore stdin handle: win32 {Marshal.GetLastWin32Error()}");
            }

            if (_windowsOpened != IntPtr.Zero && !CloseHandle(_windowsOpened))
            {
                throw new IOException(
                    $"could not close redirected stdin handle: win32 {Marshal.GetLastWin32Error()}");
            }
            return;
        }

        if (_unixSavedFd >= 0)
        {
            if (dup2(_unixSavedFd, 0) < 0)
                throw new IOException($"could not restore stdin: errno {Marshal.GetLastWin32Error()}");
        }

        var savedCloseError = _unixSavedFd >= 0 && close(_unixSavedFd) < 0
            ? Marshal.GetLastWin32Error()
            : 0;
        var openedCloseError = _unixOpenedFd >= 0 && close(_unixOpenedFd) < 0
            ? Marshal.GetLastWin32Error()
            : 0;
        if (savedCloseError != 0 || openedCloseError != 0)
        {
            throw new IOException(
                $"could not close stdin descriptors: saved errno {savedCloseError}, redirected errno {openedCloseError}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int oldfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
