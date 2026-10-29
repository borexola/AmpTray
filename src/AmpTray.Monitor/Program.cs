using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AmpTray.Device;
using AmpTray.Gpu;
using AmpTray.Linux;
using AmpTray.Logging;
using AmpTray.Platform;
using AmpTray.Service;

namespace AmpTray.Monitor;

/// <summary>
/// Headless AmpTray for Linux: the same background monitor the Windows service hosts (device and
/// GPU polling, CSV logging, auto-shutdown rules), as a console program meant for systemd. No widget.
/// </summary>
public static class Program
{
    private const string Help = """
        amptray-monitor: headless AmpTray for the Thermal Grizzly WireView Pro II (Linux).
        Polls the device (read-only) and the NVIDIA GPU, writes CSV logs and runs the auto-shutdown
        rules from service.json. Normally started by systemd (see install.sh).

          amptray-monitor            run until SIGTERM or Ctrl+C
          amptray-monitor --probe    find the device and the GPU, print one reading, exit
          amptray-monitor --config   print the settings file path
          amptray-monitor --version
        """;

    public static int Main(string[] args)
    {
        if (Has(args, "--help", "-h")) { Console.WriteLine(Help); return 0; }
        if (Has(args, "--version")) { Console.WriteLine(UpdateChecker.Current.ToString(3)); return 0; }
        if (Has(args, "--config")) { Console.WriteLine(ServiceSettings.FilePath); return 0; }

        // Before anything touches the device or the shutdown rules.
        PlatformServices.Current = OperatingSystem.IsLinux() ? new LinuxPlatform() : new NullPlatform();
        if (!OperatingSystem.IsLinux())
            Console.Error.WriteLine("Note: this is not Linux, so there is no device lookup and no shutdown here. Use the AmpTray app on Windows.");

        if (Has(args, "--probe")) return Probe();

        // Make sure there is a settings file to edit. Rules stay disarmed until the user turns them on.
        try
        {
            Directory.CreateDirectory(ServiceSettings.Folder);
            if (!File.Exists(ServiceSettings.FilePath)) new ServiceSettings().TrySave();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot use {ServiceSettings.Folder}: {ex.Message} (run as root, normally through systemd)");
            return 2;
        }

        // Every service.log line also goes to stdout, so journalctl shows it.
        ServiceSettings.Mirror = (level, message) => Console.WriteLine(level >= LogLevel.Warning ? $"[{level}] {message}" : message);
        Console.WriteLine($"amptray-monitor {UpdateChecker.Current.ToString(3)}: settings {ServiceSettings.FilePath}, log {ServiceSettings.LogFile}");

        using var stop = new ManualResetEventSlim();
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); });
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });

        using (var monitor = new BackgroundMonitor())
        {
            monitor.Start();
            stop.Wait();
            ServiceSettings.Log("Monitor stopping");
        }
        return 0;
    }

    /// <summary>One pass over the device and the GPU, for checking an installation. Exit code 1 when the device is missing.</summary>
    private static int Probe()
    {
        int rc = 0;
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        Console.WriteLine($"Serial ports present: {(ports.Length == 0 ? "none" : string.Join(", ", ports))}");

        using (var reader = new WireViewReader())
        {
            if (reader.TryConnect())
            {
                var s = reader.ReadSensors();
                if (s == null)
                {
                    Console.WriteLine($"WireView Pro II on {reader.PortName}, but no valid reading yet; try again.");
                    rc = 1;
                }
                else
                {
                    double watts = 0;
                    for (int i = 0; i < SensorSnapshot.PinCount; i++) watts += s.PinVoltage[i] * s.PinCurrent[i];
                    Console.WriteLine($"WireView Pro II on {reader.PortName}: {watts:F0} W, wires {string.Join(" ", s.PinCurrent.Select(a => a.ToString("F1")))} A, "
                                    + $"temps in {s.TempInC:F1} / out {s.TempOutC:F1} C, faults 0x{s.FaultStatus:X2}, cable {s.CableRatingW} W");
                }
            }
            else
            {
                Console.WriteLine("WireView Pro II not found. Is it connected by USB, and may this user open /dev/ttyACM*? (root, or group dialout with the udev rule installed)");
                rc = 1;
            }
        }

        using (var gpu = new GpuPoller(500))
        {
            using var got = new ManualResetEventSlim();
            GpuSnapshot? snap = null;
            gpu.Sample += s => { snap = s; got.Set(); };
            gpu.Start();
            got.Wait(3000);
            Console.WriteLine(gpu.Available && snap != null
                ? $"GPU: {snap.Name} (driver {snap.DriverVersion}), {snap.TempC:F0} C, {snap.PowerW:F0} W of {snap.PowerLimitW:F0} W, load {snap.LoadPct:F0} %"
                : $"GPU: not available ({gpu.Unavailable ?? "no sample yet"})");
        }
        return rc;
    }

    private static bool Has(string[] args, params string[] names) => args.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));
}
