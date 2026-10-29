using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AmpTray.Platform;

namespace AmpTray.Linux;

/// <summary>
/// Linux implementation of the OS services the core needs, for the root service and the desktop
/// app alike: serial ports are found through sysfs, the device is shared through a flock lock,
/// power-off goes through systemctl (or shutdown/poweroff). There is no frame counter.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    private readonly object _gate = new();
    private Timer? _pending; // a delayed shutdown counted down here, see StartShutdown

    public IReadOnlyCollection<string> FindUsbSerialPorts(ushort vendorId, ushort productId) =>
        UsbSerial.Find("/sys/class/tty", "/dev", vendorId, productId);

    /// <summary>
    /// The flock lock on /run/amptray/device.lock. The root service creates the file when it is
    /// missing (install.sh normally does, through systemd-tmpfiles). A user process without the
    /// file only has a problem worth reporting when a service exists to share the device with.
    /// </summary>
    public IDeviceLock? OpenDeviceLock(out string? problem)
    {
        if (!OperatingSystem.IsLinux())
        {
            problem = null;
            return null;
        }

        var shared = FlockDeviceLock.TryOpen(FlockDeviceLock.DefaultPath, out problem);
        if (shared != null) return shared;

        if (Environment.IsPrivilegedProcess && FlockDeviceLock.TryCreate(FlockDeviceLock.DefaultPath))
        {
            shared = FlockDeviceLock.TryOpen(FlockDeviceLock.DefaultPath, out problem);
            if (shared != null) return shared;
        }

        if (!SystemdService.IsInstalled()) problem = null;
        return null;
    }

    /// <summary>
    /// shutdown(8) only counts whole minutes, so a delay is timed here and <see cref="AbortShutdown"/>
    /// cancels it; signed-in users are told through wall. With no delay the power-off starts at once.
    /// </summary>
    public bool StartShutdown(int delaySeconds, string comment, Action<string> onFailure)
    {
        var cmd = PowerOffCommand();
        if (cmd == null)
        {
            onFailure("No systemctl, shutdown or poweroff command found.");
            return false;
        }

        lock (_gate)
        {
            _pending?.Dispose();
            _pending = null;
            if (delaySeconds <= 0) return Run(cmd.Value.File, cmd.Value.Args, onFailure);

            Broadcast($"{comment}. The system powers off in {delaySeconds} s.");
            _pending = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_pending == null) return; // aborted just before firing
                    _pending.Dispose();
                    _pending = null;
                }
                Run(cmd.Value.File, cmd.Value.Args, onFailure);
            }, null, delaySeconds * 1000, Timeout.Infinite);
            return true;
        }
    }

    public void AbortShutdown()
    {
        lock (_gate)
        {
            if (_pending == null) return;
            _pending.Dispose();
            _pending = null;
        }
        Broadcast("AmpTray: the scheduled power-off was cancelled.");
    }

    public IFpsSource CreateFpsSource() => new NoFpsSource();

    public void TrimMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch
        {
            // Purely cosmetic.
        }
    }

    private static (string File, string Args)? PowerOffCommand()
    {
        foreach (var p in new[] { "/usr/bin/systemctl", "/bin/systemctl" })
            if (File.Exists(p)) return (p, "poweroff");
        foreach (var p in new[] { "/sbin/shutdown", "/usr/sbin/shutdown" })
            if (File.Exists(p)) return (p, "-h now");
        foreach (var p in new[] { "/sbin/poweroff", "/usr/sbin/poweroff" })
            if (File.Exists(p)) return (p, "");
        return null;
    }

    /// <summary>Starts the command; a non-zero exit is reported later through onFailure, like the Windows side.</summary>
    private static bool Run(string file, string args, Action<string> onFailure)
    {
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
        }
        catch (Exception ex)
        {
            onFailure(ex.Message);
            return false;
        }
        if (p == null)
        {
            onFailure($"{file} could not be started.");
            return false;
        }

        _ = Task.Run(() =>
        {
            using (p)
            {
                string err = "";
                try { err = p.StandardError.ReadToEnd().Trim(); } catch { }
                if (p.WaitForExit(10000) && p.ExitCode != 0)
                    onFailure($"{Path.GetFileName(file)} exited with code {p.ExitCode}{(err.Length > 0 ? ": " + err : "")}.");
            }
        });
        return true;
    }

    /// <summary>Message to every terminal, as shutdown(8) would do. Best effort.</summary>
    private static void Broadcast(string message)
    {
        try
        {
            string? wall = null;
            foreach (var p in new[] { "/usr/bin/wall", "/bin/wall" })
                if (File.Exists(p)) { wall = p; break; }
            if (wall == null) return;
            using var proc = Process.Start(new ProcessStartInfo(wall)
            {
                ArgumentList = { "-n", message },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(2000);
        }
        catch
        {
            // Nobody to tell.
        }
    }
}
