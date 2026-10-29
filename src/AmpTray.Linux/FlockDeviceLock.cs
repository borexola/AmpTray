using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using AmpTray.Platform;

namespace AmpTray.Linux;

/// <summary>
/// The cross-process device lock on Linux: flock(2) on a shared file, taken around each exchange
/// with the WireView, so the root service and a user's desktop app take turns on the serial port
/// (the Linux counterpart of the Global\Access_USB_Sensors mutex on Windows).
///
/// Raw libc rather than FileStream: .NET holds a shared flock for the lifetime of any FileStream
/// opened with sharing, which would block the other process for as long as the handle lives.
/// flock works on a read-only descriptor, so a 0644 root-owned file is enough for every user.
/// The kernel drops the lock when the descriptor closes, including on a crash: nothing goes stale.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class FlockDeviceLock : IDeviceLock
{
    /// <summary>Created by systemd-tmpfiles (build/linux/amptray.conf) or by the root service on first start.</summary>
    public const string DefaultPath = "/run/amptray/device.lock";

    private const int PollMs = 5;

    private readonly int _fd;
    private readonly SemaphoreSlim _gate = new(1, 1); // flock is per open file description, not per thread
    private bool _disposed;

    private FlockDeviceLock(int fd) => _fd = fd;

    /// <summary>Opens the lock file read-only. Null when it is missing or unreadable, with the reason in <paramref name="problem"/>.</summary>
    public static FlockDeviceLock? TryOpen(string path, out string? problem)
    {
        problem = null;
        int fd = Libc.open(path, Libc.O_RDONLY | Libc.O_CLOEXEC, 0);
        if (fd >= 0) return new FlockDeviceLock(fd);

        int errno = Libc.LastErrno();
        problem = errno == Libc.ENOENT
            ? $"The shared device lock {path} is missing (install.sh creates it); polling runs without it."
            : $"Cannot open the shared device lock {path}: {Libc.Describe(errno)}; polling runs without it.";
        return null;
    }

    /// <summary>Creates the directory and the file with the modes every user can use. Needs root for the default path.</summary>
    public static bool TryCreate(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Wait(int timeoutMs)
    {
        if (_disposed) return false;
        if (!_gate.Wait(timeoutMs)) return false;

        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            if (Libc.flock(_fd, Libc.LOCK_EX | Libc.LOCK_NB) == 0) return true;
            int errno = Libc.LastErrno();
            if (errno != Libc.EAGAIN && errno != Libc.EINTR) break;      // the descriptor is gone or the file system refuses locks
            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(PollMs);
        }
        _gate.Release();
        return false;
    }

    public void Release()
    {
        if (_disposed) return;
        Libc.flock(_fd, Libc.LOCK_UN);
        _gate.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Libc.close(_fd); // releases any lock still held
        _gate.Dispose();
    }
}
