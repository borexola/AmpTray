using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AmpTray.Linux;

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
