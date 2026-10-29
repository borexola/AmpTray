using System;
using System.Diagnostics;
using System.IO;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>Desktop notifications through notify-send (libnotify). Silent when it is not installed.</summary>
public static class Notifier
{
    public enum Urgency { Low, Normal, Critical }

    private static readonly string? Exe = Find("notify-send");

    public static bool IsAvailable => Exe != null;

    public static void Send(string title, string body, Urgency urgency = Urgency.Normal, int timeoutMs = 8000)
    {
        if (Exe == null) return;
        try
        {
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-a"); psi.ArgumentList.Add("AmpTray");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("amptray");
            psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(urgency.ToString().ToLowerInvariant());
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(timeoutMs.ToString());
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(body);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("notify", "failed", "Could not send a desktop notification", ("error", ex.Message));
        }
    }

    internal static string? Find(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
