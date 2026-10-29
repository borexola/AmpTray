using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AmpTray.Service;
using Microsoft.Win32;

namespace AmpTray.Platform;

/// <summary>Windows implementation of the OS services the core needs.</summary>
public sealed class WindowsPlatform : IPlatform
{
    /// <summary>The mutex the official app and other sensor tools take around their own device access.</summary>
    private const string SharedMutexName = @"Global\Access_USB_Sensors";

    /// <summary>
    /// COM port names recorded under the USB enumerator for this VID/PID, including variants such as
    /// "...&amp;MI_00" (composite) or "...&amp;REV_0200". The registry also remembers unplugged devices.
    /// </summary>
    public IReadOnlyCollection<string> FindUsbSerialPorts(ushort vendorId, ushort productId)
    {
        string prefix = $"VID_{vendorId:X4}&PID_{productId:X4}";
        var found = new List<string>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return found;
            foreach (var hwId in usb.GetSubKeyNames().Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                using var dev = usb.OpenSubKey(hwId);
                if (dev == null) continue;
                foreach (var instance in dev.GetSubKeyNames())
                {
                    using var p = dev.OpenSubKey($@"{instance}\Device Parameters");
                    if (p?.GetValue("PortName") is string name && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                        found.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable enumerator key: no candidates this scan.
        }
        return found;
    }

    /// <summary>
    /// The shared mutex may already exist with an ACL we cannot create against (a tool running
    /// elevated made it). Try to open it instead; if that fails too, run without it rather than dying.
    /// </summary>
    public IDeviceLock? OpenDeviceLock(out string? problem)
    {
        problem = null;
        try
        {
            return new MutexLock(new Mutex(false, SharedMutexName));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try { return new MutexLock(Mutex.OpenExisting(SharedMutexName)); }
            catch (Exception ex2) { problem = ex2.Message; return null; }
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// shutdown.exe /s /f: /f closes apps that would otherwise block it with save prompts. With a delay
    /// Windows shows its own countdown to signed-in users and <see cref="AbortShutdown"/> can call it off.
    /// A non-zero exit (e.g. 1190 = a shutdown is already scheduled) is reported through onFailure.
    /// </summary>
    public bool StartShutdown(int delaySeconds, string comment, Action<string> onFailure)
    {
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo
            {
                FileName = ShutdownExe,
                Arguments = $"/s /f /t {delaySeconds} /c \"{comment}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            onFailure(ex.Message);
            return false;
        }
        if (p == null)
        {
            onFailure("shutdown.exe could not be started.");
            return false;
        }

        // Don't block the caller (often the UI thread) on the exit code.
        _ = Task.Run(() =>
        {
            using (p)
            {
                if (p.WaitForExit(5000) && p.ExitCode != 0)
                    onFailure($"shutdown.exe exited with code {p.ExitCode}.");
            }
        });
        return true;
    }

    public void AbortShutdown()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ShutdownExe, "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(5000);
        }
        catch
        {
            // Nothing scheduled, or no permission: nothing to undo.
        }
    }

    /// <summary>PresentMon (shipped next to the exe) is the reference frame counter; the built-in ETW tracker is the fallback.</summary>
    public IFpsSource CreateFpsSource() => new WindowsFpsSource();

    public void TrimMemory() => Native.TrimMemory();

    private const int RESTART_NO_PATCH = 4;
    private const int RESTART_NO_REBOOT = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// <summary>Asks Windows Error Reporting to relaunch the app after a crash or hang, with these arguments.</summary>
    public static void RegisterRestart(string commandLine)
    {
        try { RegisterApplicationRestart(commandLine, RESTART_NO_PATCH | RESTART_NO_REBOOT); }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
    }

    private static string ShutdownExe => Path.Combine(Environment.SystemDirectory, "shutdown.exe");

    private sealed class MutexLock : IDeviceLock
    {
        private readonly Mutex _mutex;

        public MutexLock(Mutex mutex) => _mutex = mutex;

        public bool Wait(int timeoutMs)
        {
            try { return _mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { return true; } // the previous owner died; we own it now
        }

        public void Release() => _mutex.ReleaseMutex();

        public void Dispose() => _mutex.Dispose();
    }

    private sealed class WindowsFpsSource : IFpsSource
    {
        private readonly PresentMonRunner _presentMon = new();
        private readonly FpsTracker _etw = new();

        public bool Active => _presentMon.Active || _etw.Active;

        public void SetInterval(int ms)
        {
            _presentMon.SetInterval(ms);
            _etw.SetInterval(ms);
        }

        public void Start()
        {
            if (PresentMonRunner.IsAvailable) _presentMon.Start();
            if (!_presentMon.Active) _etw.Start();
        }

        public void Dispose()
        {
            _presentMon.Dispose();
            _etw.Dispose();
        }
    }
}
