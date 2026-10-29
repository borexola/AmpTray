using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AmpTray.Logging;

/// <summary>
/// Brings the widget back after a crash. Two layers: the OS restarts the process after a native
/// crash or a hang where the platform supports it (Windows Error Reporting), and the managed handlers log the
/// exception, record it and start a fresh copy themselves. A crash loop is cut short: after
/// <see cref="MaxCrashes"/> crashes within <see cref="Window"/> the app stays down and says so.
/// </summary>
public static class CrashRecovery
{
    public const int MaxCrashes = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const string RecoveredSwitch = "/recovered";

    /// <summary>Where recent crash times are kept so a loop can be recognised across restarts.</summary>
    internal static string HistoryPath { get; set; } = Path.Combine(Settings.AppDataFolder, "crashes.json");

    /// <summary>Restart unless this would be the fourth crash inside the window.</summary>
    public static bool ShouldRestart(IEnumerable<DateTime> recentCrashes, DateTime now) =>
        recentCrashes.Count(t => t <= now && now - t < Window) < MaxCrashes;

    /// <summary>
    /// Logs the exception, records the crash and either starts a new copy of the app or gives up.
    /// Returns true when a restart was launched. Never throws.
    /// </summary>
    public static bool Handle(Exception? exception, string stage, Action? beforeRestart = null)
    {
        var now = DateTime.Now;
        List<DateTime> history;
        try
        {
            history = Record(now);
        }
        catch
        {
            history = new List<DateTime> { now };
        }

        bool restart = ShouldRestart(history.Where(t => t < now), now);
        AppLog.Error("crash", "unhandled", restart ? "The widget crashed and is restarting" : "The widget crashed repeatedly and stays closed",
            exception, ("stage", stage), ("crashesInWindow", history.Count(t => now - t < Window)));

        if (!restart) return false;
        try
        {
            beforeRestart?.Invoke();
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown");
            Process.Start(new ProcessStartInfo(exe, RecoveredSwitch) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("crash", "restart-failed", "Could not start a new copy of the widget", ex);
            return false;
        }
    }

    /// <summary>Appends a crash time to the history file and returns the times still inside the window.</summary>
    internal static List<DateTime> Record(DateTime now)
    {
        var times = new List<DateTime>();
        try
        {
            if (File.Exists(HistoryPath))
                times = JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(HistoryPath)) ?? new List<DateTime>();
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException) { }
        times.Add(now);
        times = times.Where(t => now - t < Window && t <= now).OrderBy(t => t).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(times));
        return times;
    }
}
