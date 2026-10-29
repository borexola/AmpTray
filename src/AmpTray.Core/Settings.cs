using System;
using System.IO;
using System.Text.Json;

namespace AmpTray;

public sealed class Settings
{
    // Window
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public double Opacity { get; set; } = 1.0;      // 0.05 .. 1.0
    public bool OpaqueOnHover { get; set; } = true; // fade back to 100 % while the mouse is over the widget
    public int PollMs { get; set; } = 1000;
    public bool Visible { get; set; } = true;
    public bool Expanded { get; set; }
    public string Language { get; set; } = Loc.Auto; // "auto", "en", "fr", "da", "pl"
    public bool CheckUpdates { get; set; } = true;   // one anonymous GitHub releases query per launch
    public bool StartupConfigured { get; set; }      // first run registers "Start with Windows" once; the user can turn it off

    // Panels
    public bool ShowWires { get; set; } = true;
    public bool ShowTemps { get; set; } = true;
    public bool ShowGraph { get; set; } = true;
    public bool ShowFaults { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowGpuTemp { get; set; } = true;  // top bar: GPU (and memory) temperature
    public bool ShowFps { get; set; } = true;      // top bar: FPS counter (needs the background service)
    public int GpuIndex { get; set; } // which NVIDIA GPU, when there are several
    public string Monitor { get; set; } = "";      // display device name the widget lives on; "" = wherever it was left
    public int GraphWindowSeconds { get; set; } = 300;

    // Theme
    public string ThemeName { get; set; } = "Graphite";
    public ThemeColors CustomColors { get; set; } = new();

    // Alerts
    public bool FlashScreen { get; set; } = true;
    public bool FlashWidget { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool BringToFront { get; set; } = true;
    public bool SoundAlarm { get; set; } = true;
    public int AlarmVolume { get; set; } = 60; // percent

    // Logging
    public bool LogEnabled { get; set; }
    public int LogIntervalSeconds { get; set; } // 0 = every sample
    public string LogFolder { get; set; } = DefaultLogFolder;

    // Automatic PC shutdown (off by default)
    public ShutdownSettings Shutdown { get; set; } = new();

    public const double MinOpacity = 0.05;

    // SpecialFolderOption.Create: on Linux GetFolderPath returns "" while ~/.config or ~/Documents
    // does not exist yet, which would put these files in the working directory. Windows always has them.
    public static string DefaultLogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create), "AmpTray", "Logs");

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AmpTray");

    /// <summary>Where settings live; tests point this at a scratch folder.</summary>
    internal static string FilePath { get; set; } = Path.Combine(AppDataFolder, "settings.json");

    /// <summary>Settings file of the early "WireView Widget" builds, carried over on first run.</summary>
    internal static string LegacyFilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "WireViewWidget", "settings.json");

    public static Settings Load()
    {
        try
        {
            MigrateFromOldName();
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                return Normalize(s);
            }
        }
        catch
        {
            // Corrupt file: fall back to defaults.
        }
        return new Settings();
    }

    /// <summary>Brings a loaded (possibly hand-edited) file back into the ranges the app can run with.</summary>
    internal static Settings Normalize(Settings s)
    {
        // A hand-edited file can null out nested objects; never let that crash startup.
        s.CustomColors ??= new ThemeColors();
        s.Shutdown ??= new ShutdownSettings();
        if (string.IsNullOrWhiteSpace(s.LogFolder)) s.LogFolder = DefaultLogFolder;
        s.PollMs = Math.Clamp(s.PollMs, 250, 5000);
        s.GraphWindowSeconds = Math.Clamp(s.GraphWindowSeconds, 30, (int)PowerHistory.MaxAge.TotalSeconds);
        s.Opacity = double.IsFinite(s.Opacity) ? Math.Clamp(s.Opacity, MinOpacity, 1.0) : 1.0;
        s.AlarmVolume = Math.Clamp(s.AlarmVolume, 5, 100);
        s.GpuIndex = Math.Max(0, s.GpuIndex);
        s.LogIntervalSeconds = Math.Max(0, s.LogIntervalSeconds);
        return s;
    }

    /// <summary>Early builds were called "WireView Widget"; carry their settings over once.</summary>
    private static void MigrateFromOldName()
    {
        if (File.Exists(FilePath)) return;
        string old = LegacyFilePath;
        if (!File.Exists(old)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.Copy(old, FilePath);
        }
        catch
        {
            // Start with defaults instead.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-then-rename so a crash or power cut mid-write can't leave a truncated file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Non-fatal: settings just won't persist.
        }
    }
}
