using System;
using System.IO;
using System.Text.Json;

namespace AmpTray.Service;

/// <summary>
/// The subset of settings the background service needs, stored machine-wide in ProgramData so the
/// service (which runs as LocalSystem, before anyone signs in) can read what the signed-in user chose.
/// The widget rewrites this file whenever its settings change; the service reloads it when it changes.
/// </summary>
public sealed class ServiceSettings
{
    public int PollMs { get; set; } = 1000;
    public int GpuIndex { get; set; }
    public bool LogEnabled { get; set; }
    public int LogIntervalSeconds { get; set; }
    public string LogFolder { get; set; } = Path.Combine(Folder, "Logs");
    public ShutdownSettings Shutdown { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AmpTray");
    public static string FilePath => Path.Combine(Folder, "service.json");
    public static string LogFile => Path.Combine(Folder, "service.log");

    public static ServiceSettings FromUser(Settings s) => new()
    {
        PollMs = s.PollMs,
        GpuIndex = s.GpuIndex,
        LogEnabled = s.LogEnabled,
        LogIntervalSeconds = s.LogIntervalSeconds,
        LogFolder = s.LogFolder,
        Shutdown = s.Shutdown,
    };

    public static ServiceSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(FilePath)) ?? new ServiceSettings();
                s.Shutdown ??= new ShutdownSettings();
                s.PollMs = Math.Clamp(s.PollMs, 250, 5000);
                if (string.IsNullOrWhiteSpace(s.LogFolder)) s.LogFolder = Path.Combine(Folder, "Logs");
                return s;
            }
        }
        catch
        {
            // Corrupt file: run with safe defaults (shutdown rules disarmed).
        }
        return new ServiceSettings();
    }

    /// <summary>Writes atomically. Returns false when the folder is missing or not writable (service not installed).</summary>
    public bool TrySave()
    {
        try
        {
            if (!Directory.Exists(Folder)) return false;
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more to do.
        }
    }
}
