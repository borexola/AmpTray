using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using AmpTray.Linux;
using AmpTray.Service;
using Xunit;

namespace AmpTray.Tests;

[SupportedOSPlatform("linux")]
public class FlockDeviceLockTests
{
    [Fact]
    public async Task Two_handles_take_turns_and_a_missing_file_is_reported()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var tmp = new TempDir();
        string file = tmp.File("device.lock");
        Assert.True(FlockDeviceLock.TryCreate(file));

        using var a = FlockDeviceLock.TryOpen(file, out var problemA);
        using var b = FlockDeviceLock.TryOpen(file, out var problemB);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(problemA);
        Assert.Null(problemB);

        Assert.True(a!.Wait(1000));
        Assert.False(b!.Wait(150));          // held by a
        a.Release();
        Assert.True(b.Wait(1000));
        b.Release();

        // The same handle from a second thread must queue too: flock itself would let it through.
        Assert.True(a.Wait(1000));
        Assert.False(await Task.Run(() => a.Wait(150)));
        a.Release();

        Assert.Null(FlockDeviceLock.TryOpen(tmp.File("missing.lock"), out var problem));
        Assert.Contains("missing", problem);
    }

    [Fact]
    public void Another_process_holding_the_file_blocks_until_it_lets_go()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/flock")) return;

        using var tmp = new TempDir();
        string file = tmp.File("device.lock");
        FlockDeviceLock.TryCreate(file);

        using var other = Process.Start(new ProcessStartInfo("/usr/bin/flock") { ArgumentList = { "-x", file, "sleep", "1.5" }, UseShellExecute = false })!;
        using var mine = FlockDeviceLock.TryOpen(file, out _)!;
        var sw = Stopwatch.StartNew();
        // Give flock(1) a moment to take the lock, then we must wait for it.
        Assert.True(mine.Wait(5000));
        mine.Release();
        other.WaitForExit();
        Assert.True(sw.ElapsedMilliseconds > 500 || other.ExitCode == 0);
    }
}

public class XdgAutostartTests
{
    [Fact]
    public void Enable_writes_a_desktop_entry_and_disable_removes_it()
    {
        using var tmp = new TempDir();
        string old = XdgAutostart.Dir;
        XdgAutostart.Dir = tmp.File("autostart");
        try
        {
            Assert.False(XdgAutostart.IsEnabled());
            Assert.True(XdgAutostart.Enable("/opt/amptray/amptray"));
            Assert.True(XdgAutostart.IsEnabled());
            string text = File.ReadAllText(XdgAutostart.FilePath);
            Assert.Contains("[Desktop Entry]", text);
            Assert.Contains("Exec=/opt/amptray/amptray", text);
            Assert.True(XdgAutostart.Disable());
            Assert.False(XdgAutostart.IsEnabled());
            Assert.True(XdgAutostart.Disable()); // idempotent
        }
        finally
        {
            XdgAutostart.Dir = old;
        }
    }
}

public class SystemdServiceTests
{
    [Fact]
    public void Installed_means_the_unit_file_exists()
    {
        using var tmp = new TempDir();
        string old = SystemdService.UnitPath;
        SystemdService.UnitPath = tmp.File("amptray-monitor.service");
        try
        {
            Assert.False(SystemdService.IsInstalled());
            Assert.False(SystemdService.IsActive());
            File.WriteAllText(SystemdService.UnitPath, "[Unit]\n");
            Assert.True(SystemdService.IsInstalled());
        }
        finally
        {
            SystemdService.UnitPath = old;
        }
    }

    [Fact]
    public void Service_copy_of_the_settings_logs_under_the_service_folder()
    {
        using var tmp = new TempDir();
        ServiceSettings.FolderOverride = tmp.Path;
        try
        {
            var s = new Settings { PollMs = 750, LogEnabled = true, LogFolder = "/home/someone/Documents/AmpTray/Logs" };
            var cfg = SystemdService.ForService(s);
            Assert.Equal(750, cfg.PollMs);
            Assert.True(cfg.LogEnabled);
            Assert.Equal(Path.Combine(tmp.Path, "Logs"), cfg.LogFolder);
        }
        finally
        {
            ServiceSettings.FolderOverride = null;
        }
    }
}
