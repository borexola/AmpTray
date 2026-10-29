using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace AmpTray.Linux;

/// <summary>
/// One desktop app per user: a flock on a file in the runtime directory (the kernel releases it
/// when the process dies, so nothing goes stale). A second launch leaves a flag file for the
/// first instance, which watches for it and shows its widget, then exits.
/// </summary>
[SupportedOSPlatform("linux")]
public static class SingleInstance
{
    private const string LockName = "amptray.lock";
    private const string ShowName = "amptray-show.flag";

    private static int _fd = -1;

    /// <summary>$XDG_RUNTIME_DIR (per user, tmpfs), else the settings folder. Tests point this elsewhere.</summary>
    public static string Dir { get; internal set; } = DefaultDir();

    public static string LockPath => Path.Combine(Dir, LockName);
    public static string ShowFlagPath => Path.Combine(Dir, ShowName);

    /// <summary>True when this process now owns the instance lock. False means another copy runs and has been asked to show itself.</summary>
    public static bool TryAcquire()
    {
        try { Directory.CreateDirectory(Dir); } catch { return true; } // nowhere to lock: run anyway

        int fd = Libc.open(LockPath, Libc.O_RDWR | Libc.O_CREAT | Libc.O_CLOEXEC, 0x1A4 /* 0644 */);
        if (fd < 0) return true;
        if (Libc.flock(fd, Libc.LOCK_EX | Libc.LOCK_NB) == 0)
        {
            _fd = fd;
            return true;
        }
        Libc.close(fd);
        try { File.WriteAllText(ShowFlagPath, DateTime.UtcNow.ToString("O")); } catch { }
        return false;
    }

    public static void Release()
    {
        if (_fd < 0) return;
        Libc.close(_fd);
        _fd = -1;
    }

    /// <summary>Calls <paramref name="onShow"/> (on a thread-pool thread) each time another launch leaves the flag.</summary>
    public static IDisposable WatchShow(Action onShow)
    {
        var watcher = new FileSystemWatcher(Dir, ShowName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };
        Timer? debounce = null;
        void Trigger(object? _, FileSystemEventArgs __)
        {
            debounce ??= new Timer(_ =>
            {
                try { File.Delete(ShowFlagPath); } catch { }
                try { onShow(); } catch { }
            }, null, Timeout.Infinite, Timeout.Infinite);
            debounce.Change(200, Timeout.Infinite);
        }
        watcher.Created += Trigger;
        watcher.Changed += Trigger;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(ShowFlagPath)) Trigger(null, null!); // left while we were not watching
        return new Scope(watcher, () => debounce?.Dispose());
    }

    private static string DefaultDir()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)) return runtime;
        return Settings.AppDataFolder;
    }

    private sealed class Scope : IDisposable
    {
        private readonly FileSystemWatcher _w;
        private readonly Action _also;
        public Scope(FileSystemWatcher w, Action also) { _w = w; _also = also; }
        public void Dispose() { _w.Dispose(); _also(); }
    }
}
