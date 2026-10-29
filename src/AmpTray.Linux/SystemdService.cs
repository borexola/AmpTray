using System;
using System.Diagnostics;
using System.IO;
using AmpTray.Service;

namespace AmpTray.Linux;

/// <summary>
/// The desktop app's view of the amptray-monitor systemd service: whether it is installed and
/// running, and how to turn it on or off (through pkexec, so the user sees a password prompt).
/// The Linux counterpart of the Windows ServiceManager.
/// </summary>
public static class SystemdService
{
    public const string Unit = "amptray-monitor";
    public const string InstallRoot = "/opt/amptray";

    /// <summary>Tests point this at a scratch file.</summary>
    public static string UnitPath { get; internal set; } = "/etc/systemd/system/amptray-monitor.service";

    private const int CacheMs = 10000;
    private static bool _cachedActive;
    private static long _cachedAt = long.MinValue;

    public static bool IsInstalled() => File.Exists(UnitPath);

    /// <summary>systemctl is-active: true only while the unit is running.</summary>
    public static bool IsActive()
    {
        if (!IsInstalled()) return false;
        return Run("systemctl", new[] { "is-active", "--quiet", Unit }, 5000) == 0;
    }

    /// <summary>The last answer of <see cref="IsActive"/>, re-asked at most every 10 s (it is polled per sample).</summary>
    public static bool IsActiveCached()
    {
        long now = Environment.TickCount64;
        if (now - _cachedAt > CacheMs)
        {
            _cachedActive = IsActive();
            _cachedAt = now;
        }
        return _cachedActive;
    }

    public static void InvalidateCache() => _cachedAt = long.MinValue;

    /// <summary>install.sh next to the installed program, or next to this executable (unpacked tarball).</summary>
    public static string? InstallScript
    {
        get
        {
            foreach (var p in new[] { Path.Combine(InstallRoot, "install.sh"), Path.Combine(AppContext.BaseDirectory, "install.sh") })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    /// <summary>
    /// Enables and starts the service: the unit file present means just `systemctl enable --now`,
    /// otherwise the bundled install.sh does the whole installation. Both through pkexec. False
    /// when declined, when nothing can install it, or when the command failed.
    /// </summary>
    public static bool Install()
    {
        bool ok;
        if (IsInstalled()) ok = Pkexec("systemctl", "enable", "--now", Unit);
        else if (InstallScript is string script) ok = Pkexec(script);
        else ok = false;
        InvalidateCache();
        return ok;
    }

    /// <summary>Stops and disables the service; the files stay (uninstall.sh removes everything).</summary>
    public static bool Uninstall()
    {
        bool ok = !IsInstalled() || Pkexec("systemctl", "disable", "--now", Unit);
        InvalidateCache();
        return ok;
    }

    /// <summary>
    /// The user's settings as the root service should get them: the service logs under its own
    /// folder, never into the user's home, where root-owned files would block the app's own logger.
    /// </summary>
    public static ServiceSettings ForService(Settings s)
    {
        var cfg = ServiceSettings.FromUser(s);
        cfg.LogFolder = Path.Combine(ServiceSettings.Folder, "Logs");
        return cfg;
    }

    /// <summary>Whether this user may write the service settings and the cancel flag (group amptray, after install.sh and a re-login).</summary>
    public static bool SettingsFolderWritable()
    {
        try
        {
            if (!Directory.Exists(ServiceSettings.Folder)) return false;
            string probe = Path.Combine(ServiceSettings.Folder, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Pkexec(params string[] args)
    {
        int rc = Run("pkexec", args, 120000);
        return rc == 0; // 126 = dismissed, 127 = not authorised or no agent
    }

    private static int Run(string file, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return -1;
            }
            return p.ExitCode;
        }
        catch
        {
            return -1; // no such command
        }
    }
}
