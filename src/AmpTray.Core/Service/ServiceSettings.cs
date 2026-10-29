using System;
using System.IO;
using System.Linq;
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

    /// <summary>Tests point the machine-wide folder at a scratch directory.</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>ProgramData\AmpTray on Windows; /var/lib/amptray elsewhere (CommonApplicationData maps to the read-only /usr/share there).</summary>
    public static string Folder => FolderOverride ?? DefaultFolder;

    private static string DefaultFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AmpTray")
        : "/var/lib/amptray";
    public static string FilePath => Path.Combine(Folder, "service.json");
    public static string LogFile => Path.Combine(Folder, "service.log");

    /// <summary>The widget touches this file when the user cancels a shutdown; the service watches its timestamp.</summary>
    public static string CancelRequestPath => Path.Combine(Folder, "cancel-shutdown.flag");

    /// <summary>Asks the service to stand down: suppress its active rules and abort a shutdown it scheduled.</summary>
    public static bool RequestCancel()
    {
        try
        {
            if (!Directory.Exists(Folder)) return false;
            File.WriteAllText(CancelRequestPath, DateTime.UtcNow.ToString("O"));
            File.SetLastWriteTimeUtc(CancelRequestPath, DateTime.UtcNow);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static DateTime CancelRequestStamp()
    {
        try { return File.Exists(CancelRequestPath) ? File.GetLastWriteTimeUtc(CancelRequestPath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

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

    private static Logging.EventLog? _log;
    private static readonly string[] Trouble = { "fail", "error", "could not", "unexpected", "stopped", "disconnected", "shutting down" };

    /// <summary>Service events go to service.log in ProgramData, one JSON line each; the widget's Logs page reads them too.</summary>
    public static void Log(string line)
    {
        var level = Trouble.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)) ? Logging.LogLevel.Warning : Logging.LogLevel.Info;
        Log(level, "log", line);
    }

    public static void Log(Logging.LogLevel level, string @event, string message, params (string Key, object? Value)[] data)
    {
        _log ??= new Logging.EventLog(LogFile);
        _log.Write(level, "service", @event, message, data);
        try { Mirror?.Invoke(level, message); } catch { }
    }

    /// <summary>Also receives every log line; the console host points this at stdout so the journal shows them.</summary>
    public static Action<Logging.LogLevel, string>? Mirror { get; set; }
}
