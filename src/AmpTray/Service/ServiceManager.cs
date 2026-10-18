using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace AmpTray.Service;

/// <summary>Installs, removes and queries the "AmpTray" Windows service using sc.exe; needs an elevated process.</summary>
public static class ServiceManager
{
    public const string Name = "AmpTray";
    private const string DisplayName = "AmpTray background monitor";
    private const string Description = "Keeps the AmpTray power-cable logging and auto-shutdown rules running from boot, before anyone signs in. Read-only towards the WireView device.";

    private static DateTime _statusStamp;
    private static bool _running;

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(Name);
            _ = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>IsRunning, re-checked at most every 10 s (called from the widget's sample loop).</summary>
    public static bool IsRunningCached()
    {
        if ((DateTime.UtcNow - _statusStamp).TotalSeconds > 10)
        {
            _running = IsRunning();
            _statusStamp = DateTime.UtcNow;
        }
        return _running;
    }

    /// <summary>Elevated entry point (AmpTray.exe /install-service). Returns a process exit code.</summary>
    public static int Install()
    {
        try
        {
            // Machine-wide settings folder; signed-in users must be able to update service.json.
            Directory.CreateDirectory(ServiceSettings.Folder);
            Run("icacls.exe", $"\"{ServiceSettings.Folder}\" /grant *S-1-5-32-545:(OI)(CI)M /T /Q"); // BUILTIN\Users: modify
            if (!File.Exists(ServiceSettings.FilePath))
            {
                // Seed from the invoking user's settings when there are any (UAC keeps the same user profile).
                var seed = File.Exists(Path.Combine(Settings.AppDataFolder, "settings.json"))
                    ? ServiceSettings.FromUser(Settings.Load())
                    : new ServiceSettings();
                seed.TrySave();
            }

            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
            string bin = $"\\\"{exe}\\\" /service";
            if (IsInstalled())
                Check(Run("sc.exe", $"config {Name} binPath= \"{bin}\" start= auto DisplayName= \"{DisplayName}\" obj= LocalSystem"));
            else
                Check(Run("sc.exe", $"create {Name} binPath= \"{bin}\" start= auto DisplayName= \"{DisplayName}\" obj= LocalSystem"));
            Run("sc.exe", $"description {Name} \"{Description}\"");
            Run("sc.exe", $"failure {Name} reset= 86400 actions= restart/5000/restart/30000/restart/60000");
            if (!IsRunning()) Run("sc.exe", $"start {Name}");
            ServiceSettings.Log($"Service installed from {exe}");
            return 0;
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("Service install failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Elevated entry point (AmpTray.exe /uninstall-service).</summary>
    public static int Uninstall()
    {
        try
        {
            if (!IsInstalled()) return 0;
            Run("sc.exe", $"stop {Name}");
            for (int i = 0; i < 30 && IsRunning(); i++) Thread.Sleep(500);
            Check(Run("sc.exe", $"delete {Name}"));
            ServiceSettings.Log("Service removed");
            return 0;
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("Service removal failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Runs this exe elevated with the given switch (UAC prompt) and waits for it.
    /// Returns false if the user declined or the command failed.
    /// </summary>
    public static bool RunElevated(string switchArg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, switchArg) { UseShellExecute = true, Verb = "runas" });
            if (p == null) return false;
            p.WaitForExit(120_000);
            _statusStamp = DateTime.MinValue; // force a fresh status read
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC declined
        }
        catch
        {
            return false;
        }
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{file} could not be started.");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, output.Trim());
    }

    private static void Check((int Code, string Output) r)
    {
        if (r.Code != 0) throw new InvalidOperationException(r.Output.Length > 0 ? r.Output : $"exit code {r.Code}");
    }
}
