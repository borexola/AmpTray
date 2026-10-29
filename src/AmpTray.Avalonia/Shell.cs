using System;
using System.Diagnostics;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>Hands URLs and folders to the desktop (xdg-open). Best effort.</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "open-failed", "Could not open " + target, ("error", ex.Message));
        }
    }
}
