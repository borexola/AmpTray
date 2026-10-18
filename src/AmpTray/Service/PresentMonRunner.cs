using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private static readonly string[] Ignored = { "dwm", "csrss", "AmpTray" };

    private sealed class Chain
    {
        public readonly Queue<double> Times = new(); // CSV time column, seconds
        public double Latest;
    }

    private sealed class Proc
    {
        public readonly Dictionary<ulong, Chain> Chains = new();
        public string Name = "";
        public long LastSeen;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Proc> _procs = new();
    private Process? _proc;
    private Thread? _reader;
    private Timer? _publish;
    private long _rows;
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
            int iPid = -1, iChain = -1, iTime = -1, iApp = -1;
            string? line;
            while ((line = stdout.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (iPid < 0)
                {
                    // Header row: locate the columns by name so either metrics layout works.
                    var cols = line.Split(',');
                    iApp = Array.IndexOf(cols, "Application");
                    iPid = Array.IndexOf(cols, "ProcessID");
                    iChain = Array.IndexOf(cols, "SwapChainAddress");
                    iTime = Array.IndexOf(cols, "TimeInSeconds");
                    if (iTime < 0) iTime = Array.IndexOf(cols, "CPUStartTime");
                    if (iPid < 0 || iChain < 0 || iTime < 0)
                    {
                        ServiceSettings.Log("PresentMon: unexpected CSV header: " + line);
                        iPid = -1;
                    }
                    continue;
                }

                var f = line.Split(',');
                if (f.Length <= Math.Max(iTime, Math.Max(iPid, iChain))) continue;
                if (!int.TryParse(f[iPid], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
                if (!double.TryParse(f[iTime], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) continue;
                string chainText = f[iChain].Trim();
                ulong chain = chainText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.Parse(chainText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : (ulong.TryParse(chainText, out var c) ? c : 0UL);

                lock (_gate)
                {
                    if (!_procs.TryGetValue(pid, out var p))
                    {
                        _procs[pid] = p = new Proc();
                        string app = iApp >= 0 && iApp < f.Length ? f[iApp] : "";
                        p.Name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
                    }
                    if (!p.Chains.TryGetValue(chain, out var ch)) p.Chains[chain] = ch = new Chain();
                    ch.Times.Enqueue(t);
                    if (t > ch.Latest) ch.Latest = t;
                    while (ch.Times.Count > 0 && ch.Times.Peek() < ch.Latest - 1.0) ch.Times.Dequeue();
                    p.LastSeen = Stopwatch.GetTimestamp();
                    _rows++;
                }
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
        var entries = new List<FpsEntry>();
        lock (_gate)
        {
            foreach (var (pid, p) in _procs.ToList())
            {
                if (now - p.LastSeen > 5 * second) { _procs.Remove(pid); continue; }
                // Data more than ~2 s old (by wall clock) means the game stopped presenting: report nothing.
                if (now - p.LastSeen > 2 * second) continue;
                int best = p.Chains.Values.Max(ch => ch.Times.Count);
                if (best < 2) continue;
                if (Ignored.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                entries.Add(new FpsEntry(pid, p.Name, best));
            }
        }
        new FpsSnapshot { Timestamp = DateTime.Now, Processes = entries.OrderByDescending(e => e.Fps).Take(8).ToList() }.TrySave();

        if (now - _lastDiag > 60 * second)
        {
            _lastDiag = now;
            ServiceSettings.Log($"FPS (PresentMon): rows={_rows} processes={entries.Count}" +
                (entries.Count > 0 ? " top=" + string.Join(", ", entries.OrderByDescending(e => e.Fps).Take(3).Select(e => $"{e.Name}:{e.Fps:0}")) : ""));
            _rows = 0;
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
