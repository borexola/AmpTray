using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AmpTray.Platform;

namespace AmpTray.Monitor;

/// <summary>
/// Linux implementation of the OS services the core needs. Meant to run as root under systemd:
/// serial ports are found through sysfs, power-off goes through systemctl (or shutdown/poweroff).
/// There is no shared device lock (no other Linux tool uses one) and no frame counter.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    private readonly object _gate = new();
    private Timer? _pending; // a delayed shutdown counted down here, see StartShutdown

    public IReadOnlyCollection<string> FindUsbSerialPorts(ushort vendorId, ushort productId) =>
        UsbSerial.Find("/sys/class/tty", "/dev", vendorId, productId);

    public IDeviceLock? OpenDeviceLock(out string? problem)
    {
        problem = null;
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

/// <summary>Finds USB serial ports by vendor and product ID through sysfs.</summary>
internal static class UsbSerial
{
    /// <summary>
    /// Every /sys/class/tty/ttyACM* and ttyUSB* entry links to its device under /sys/devices; the
    /// "device" link inside points at the USB interface, whose parent (the USB device) carries
    /// idVendor and idProduct. The result uses the names SerialPort.GetPortNames() reports (/dev/ttyACM0).
    /// </summary>
    internal static IReadOnlyCollection<string> Find(string sysClassTty, string devDir, ushort vendorId, ushort productId)
    {
        var found = new List<string>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(sysClassTty); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (!name.StartsWith("ttyACM", StringComparison.Ordinal) && !name.StartsWith("ttyUSB", StringComparison.Ordinal)) continue;
            try
            {
                // Resolve the class link first, then the device link: a relative link is relative to its
                // real directory, and .NET resolves it lexically against the path it was given.
                string? dir = RealPath(entry);
                if (dir != null) dir = RealPath(Path.Combine(dir, "device"));
                for (int up = 0; dir != null && up < 4; up++, dir = Path.GetDirectoryName(dir))
                {
                    string v = Path.Combine(dir, "idVendor"), p = Path.Combine(dir, "idProduct");
                    if (!File.Exists(v) || !File.Exists(p)) continue;
                    if (ReadHex(v) == vendorId && ReadHex(p) == productId) found.Add(Path.Combine(devDir, name));
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Device went away mid-scan.
            }
        }
        return found;
    }

    private static string? RealPath(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) return null;
        if (info.LinkTarget == null) return info.FullName;
        return (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
    }

    private static ushort? ReadHex(string file) =>
        ushort.TryParse(File.ReadAllText(file).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
}
