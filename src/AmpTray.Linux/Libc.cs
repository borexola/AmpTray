using System.Runtime.InteropServices;

namespace AmpTray.Linux;

/// <summary>The few libc calls the Linux side needs: file descriptors and flock(2).</summary>
internal static class Libc
{
    public const int O_RDONLY = 0;
    public const int O_RDWR = 2;
    public const int O_CREAT = 0x40;
    public const int O_CLOEXEC = 0x80000;

    public const int LOCK_SH = 1;
    public const int LOCK_EX = 2;
    public const int LOCK_NB = 4;
    public const int LOCK_UN = 8;

    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EAGAIN = 11; // == EWOULDBLOCK

    [DllImport("libc", SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    public static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    public static int LastErrno() => Marshal.GetLastPInvokeError();

    public static string Describe(int errno) => Marshal.GetPInvokeErrorMessage(errno);
}
