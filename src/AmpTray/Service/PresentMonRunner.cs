using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AmpTray.Service;

/// <summary>
/// Frame counting through Intel's PresentMon (MIT), the reference implementation other monitoring
/// tools build on. The service runs the console build shipped next to AmpTray.exe and reads its
/// per-frame CSV from stdout: one row per present per swap chain, with the process name and a time
/// stamp. Frames per second = presents in the last second on the process's busiest swap chain.
/// </summary>
public sealed class PresentMonRunner : IDisposable
{
    private const string SessionName = "AmpTrayPresentMon";

    private readonly object _gate = new();
    private readonly PresentTally _tally = new();
    private Process? _proc;
    private Thread? _reader;
    private Timer? _publish;
    private long _lastDiag = Stopwatch.GetTimestamp();

    public bool Active { get; private set; }

    private int _intervalMs = 500;

    /// <summary>How often fps.json is rewritten; follows the widget's refresh interval.</summary>
    public void SetInterval(int ms)
    {
        _intervalMs = Math.Clamp(ms, 250, 5000);
        _publish?.Change(_intervalMs, _intervalMs);
    }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");

    public static bool IsAvailable => File.Exists(ExePath);

    public void Start()
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
            {
                "--output_stdout", "--no_console_stats", "--v1_metrics",
                "--no_track_display", "--no_track_gpu", "--no_track_input",
                "--session_name", SessionName, "--stop_existing_session",
                "--exclude", "dwm.exe", "--exclude", "AmpTray.exe",
            }) psi.ArgumentList.Add(a);

            _proc = Process.Start(psi) ?? throw new InvalidOperationException("PresentMon did not start.");
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ServiceSettings.Log("PresentMon: " + e.Data); };
            _proc.BeginErrorReadLine();
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PresentMon reader" };
            _reader.Start();
            _publish = new Timer(_ => Publish(), null, _intervalMs, _intervalMs);
            Active = true;
            ServiceSettings.Log("FPS source: PresentMon (" + ExePath + ")");
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("PresentMon failed to start: " + ex.Message);
            Dispose();
        }
    }

    private void ReadLoop()
    {
        try
        {
            var stdout = _proc!.StandardOutput;
            PresentTally.Columns? columns = null;
            string? line;
            while ((line = stdout.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (columns == null)
                {
                    // Header row: locate the columns by name so either metrics layout works.
                    columns = PresentTally.ParseHeader(line);
                    if (columns == null) ServiceSettings.Log("PresentMon: unexpected CSV header: " + line);
                    continue;
                }
                lock (_gate) _tally.AddRow(line, columns, Stopwatch.GetTimestamp());
            }
            ServiceSettings.Log("PresentMon output ended" + (_proc.HasExited ? $" (exit code {_proc.ExitCode})" : ""));
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("PresentMon reader stopped: " + ex.Message);
        }
        Active = false;
    }

    private void Publish()
    {
        long now = Stopwatch.GetTimestamp();
        long second = Stopwatch.Frequency;
        List<FpsEntry> entries;
        long rows;
        lock (_gate)
        {
            entries = _tally.Entries(now, second);
            rows = _tally.Rows;
        }
        new FpsSnapshot { Timestamp = DateTime.Now, Processes = entries.OrderByDescending(e => e.Fps).Take(8).ToList() }.TrySave();

        if (now - _lastDiag > 60 * second)
        {
            _lastDiag = now;
            ServiceSettings.Log($"FPS (PresentMon): rows={rows} processes={entries.Count}" +
                (entries.Count > 0 ? " top=" + string.Join(", ", entries.OrderByDescending(e => e.Fps).Take(3).Select(e => $"{e.Name}:{e.Fps:0}")) : ""));
            lock (_gate) _tally.ResetRows();
        }
    }

    public void Dispose()
    {
        Active = false;
        _publish?.Dispose();
        _publish = null;
        try
        {
            if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
        if (_reader != null && _reader.IsAlive) _reader.Join(3000);
        _reader = null;
    }
}
