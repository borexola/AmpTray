using System;
using System.IO;

namespace AmpTray.Linux;

/// <summary>"Start at login" on Linux: a .desktop entry in the XDG autostart folder.</summary>
public static class XdgAutostart
{
    /// <summary>Tests point this at a scratch directory.</summary>
    public static string Dir { get; internal set; } = Path.Combine(ConfigHome(), "autostart");

    public static string FilePath => Path.Combine(Dir, "amptray.desktop");

    /// <summary>The installed program when there is one, else the running executable.</summary>
    public static string DefaultExec
    {
        get
        {
            string installed = Path.Combine(SystemdService.InstallRoot, "amptray");
            if (File.Exists(installed)) return installed;
            return Environment.ProcessPath ?? "amptray";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            foreach (var line in File.ReadLines(FilePath))
                if (line.Trim().Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Enable(string? exec = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=AmpTray\n" +
                "Comment=WireView Pro II power monitor\n" +
                $"Exec={exec ?? DefaultExec}\n" +
                "Icon=amptray\n" +
                "Terminal=false\n" +
                "X-GNOME-Autostart-enabled=true\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ConfigHome()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg)) return xdg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }
}
