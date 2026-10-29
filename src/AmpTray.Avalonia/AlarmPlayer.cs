using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AmpTray.Logging;
using AmpTray.Ui;

namespace AmpTray.Avalonia;

/// <summary>
/// Loops the fault alarm through whichever command-line player the desktop has (pw-play, paplay or
/// aplay). The tone is generated once per volume into the runtime directory. Silent when none exists.
/// </summary>
public sealed class AlarmPlayer : IDisposable
{
    private static readonly (string Exe, string[] Args)[] Players =
    {
        ("pw-play", Array.Empty<string>()),
        ("paplay", Array.Empty<string>()),
        ("aplay", new[] { "-q" }),
    };

    private static readonly (string Path, string[] Args)? Player = FindPlayer();

    public static bool IsAvailable => Player != null;

    private readonly object _gate = new();
    private Process? _current;
    private bool _active;
    private int _generation;

    public bool IsActive => _active;

    public void Start(int volumePercent)
    {
        if (Player == null) return;
        string file;
        try
        {
            volumePercent = Math.Clamp(volumePercent, AlarmWave.MinVolumePercent, AlarmWave.MaxVolumePercent);
            file = Path.Combine(RuntimeDir(), $"amptray-alarm-{volumePercent}.wav");
            if (!File.Exists(file)) File.WriteAllBytes(file, AlarmWave.BuildPercent(volumePercent));
        }
        catch (Exception ex)
        {
            AppLog.Warn("alarm", "wav-failed", "Could not write the alarm sound", ("error", ex.Message));
            return;
        }

        Stop();
        int gen;
        lock (_gate)
        {
            _active = true;
            gen = ++_generation;
        }
        var thread = new Thread(() => Loop(file, gen)) { IsBackground = true, Name = "alarm" };
        thread.Start();
    }

    private void Loop(string file, int gen)
    {
        var (exe, args) = Player!.Value;
        while (true)
        {
            Process? p;
            lock (_gate)
            {
                if (!_active || gen != _generation) return;
                try
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    psi.ArgumentList.Add(file);
                    p = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("alarm", "player-failed", "Could not start the alarm player", ("error", ex.Message));
                    _active = false;
                    return;
                }
                _current = p;
            }
            if (p == null) return;
            try { p.WaitForExit(); } catch { }
            p.Dispose();
            lock (_gate) { if (ReferenceEquals(_current, p)) _current = null; }
        }
    }

    public void Stop()
    {
        Process? p;
        lock (_gate)
        {
            _active = false;
            _generation++;
            p = _current;
            _current = null;
        }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(); } catch { }
    }

    public void Dispose() => Stop();

    private static (string, string[])? FindPlayer()
    {
        foreach (var (exe, args) in Players)
            if (Notifier.Find(exe) is string path) return (path, args);
        return null;
    }

    private static string RuntimeDir()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)) return runtime;
        return Settings.AppDataFolder;
    }
}
