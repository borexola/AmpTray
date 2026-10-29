using System;
using System.IO;
using AmpTray.Linux;
using Xunit;

namespace AmpTray.Tests;

public class UsbSerialTests
{
    /// <summary>
    /// A sysfs tree laid out like the kernel's: a WireView on ttyACM0 (cdc-acm), a CH340 adapter on
    /// ttyUSB0 (usb-serial sits one level deeper) and an on-board ttyS0 without USB IDs. The class
    /// entries and "device" links are relative symlinks, as on a real system.
    /// </summary>
    [Fact]
    public void Finds_ports_by_vendor_and_product_through_sysfs_links()
    {
        if (!OperatingSystem.IsLinux()) return; // symlinks need no privilege only there, and the layout is Linux-specific

        using var tmp = new TempDir();
        string sys = tmp.Path;

        string acmDev = Dir(sys, "devices/pci0000:00/0000:00:14.0/usb1/1-2");
        File.WriteAllText(Path.Combine(acmDev, "idVendor"), "0483\n");
        File.WriteAllText(Path.Combine(acmDev, "idProduct"), "5740\n");
        Directory.CreateSymbolicLink(Path.Combine(Dir(acmDev, "1-2:1.0/tty/ttyACM0"), "device"), "../../../1-2:1.0");

        string usbDev = Dir(sys, "devices/pci0000:00/0000:00:14.0/usb1/1-3");
        File.WriteAllText(Path.Combine(usbDev, "idVendor"), "1a86\n");
        File.WriteAllText(Path.Combine(usbDev, "idProduct"), "7523\n");
        Directory.CreateSymbolicLink(Path.Combine(Dir(usbDev, "1-3:1.0/ttyUSB0/tty/ttyUSB0"), "device"), "../../../ttyUSB0");

        Directory.CreateSymbolicLink(Path.Combine(Dir(sys, "devices/platform/serial8250/tty/ttyS0"), "device"), "../../../serial8250");

        string cls = Dir(sys, "class/tty");
        Directory.CreateSymbolicLink(Path.Combine(cls, "ttyACM0"), "../../devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/tty/ttyACM0");
        Directory.CreateSymbolicLink(Path.Combine(cls, "ttyUSB0"), "../../devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/ttyUSB0/tty/ttyUSB0");
        Directory.CreateSymbolicLink(Path.Combine(cls, "ttyS0"), "../../devices/platform/serial8250/tty/ttyS0");

        Assert.Equal(new[] { "/dev/ttyACM0" }, UsbSerial.Find(cls, "/dev", 0x0483, 0x5740));
        Assert.Equal(new[] { "/dev/ttyUSB0" }, UsbSerial.Find(cls, "/dev", 0x1A86, 0x7523));
        Assert.Empty(UsbSerial.Find(cls, "/dev", 0x0483, 0x0001));
        Assert.Empty(UsbSerial.Find(Path.Combine(sys, "missing"), "/dev", 0x0483, 0x5740));
    }

    private static string Dir(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(path);
        return path;
    }
}
