using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AmpTray.Service;

/// <summary>
/// The bookkeeping behind the FPS counter: PresentMon's per-frame CSV rows go in, per-process frame
/// rates come out. Frames per second = presents in the last second on the process's busiest swap
/// chain. Kept free of processes and timers so it can be exercised with canned rows.
/// </summary>
internal sealed class PresentTally
{
    internal static readonly string[] Ignored = { "dwm", "csrss", "AmpTray" };

    /// <summary>Column indexes found in the CSV header; -1 for an optional column that is absent.</summary>
    internal sealed record Columns(int Pid, int Chain, int Time, int App);

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

    private readonly Dictionary<int, Proc> _procs = new();

    /// <summary>Rows accepted since the counter was last reset (diagnostics).</summary>
    public long Rows { get; private set; }

    public void ResetRows() => Rows = 0;

    /// <summary>Locates the columns by name so either PresentMon metrics layout works; null when a required one is missing.</summary>
    public static Columns? ParseHeader(string line)
    {
        var cols = line.Split(',');
        int app = Array.IndexOf(cols, "Application");
        int pid = Array.IndexOf(cols, "ProcessID");
        int chain = Array.IndexOf(cols, "SwapChainAddress");
        int time = Array.IndexOf(cols, "TimeInSeconds");
        if (time < 0) time = Array.IndexOf(cols, "CPUStartTime");
        return pid < 0 || chain < 0 || time < 0 ? null : new Columns(pid, chain, time, app);
    }

    /// <summary>Parses one data row and records the present; false for a row that cannot be read.</summary>
    public bool AddRow(string line, Columns c, long nowTicks)
    {
        var f = line.Split(',');
        if (f.Length <= Math.Max(c.Time, Math.Max(c.Pid, c.Chain))) return false;
        if (!int.TryParse(f[c.Pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return false;
        if (!double.TryParse(f[c.Time], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) return false;
        string chainText = f[c.Chain].Trim();
        ulong chain = chainText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (ulong.TryParse(chainText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0UL)
            : (ulong.TryParse(chainText, out var dec) ? dec : 0UL);
        string app = c.App >= 0 && c.App < f.Length ? f[c.App] : "";
        Add(pid, app, chain, t, nowTicks);
        return true;
    }

    public void Add(int pid, string app, ulong chain, double seconds, long nowTicks)
    {
        if (!_procs.TryGetValue(pid, out var p))
        {
            _procs[pid] = p = new Proc();
            p.Name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        }
        if (!p.Chains.TryGetValue(chain, out var ch)) p.Chains[chain] = ch = new Chain();
        ch.Times.Enqueue(seconds);
        if (seconds > ch.Latest) ch.Latest = seconds;
        while (ch.Times.Count > 0 && ch.Times.Peek() < ch.Latest - 1.0) ch.Times.Dequeue();
        p.LastSeen = nowTicks;
        Rows++;
    }

    /// <summary>
    /// Current frame rates. A process silent for more than two seconds is left out (the game stopped
    /// presenting), one silent for more than five is forgotten.
    /// </summary>
    public List<FpsEntry> Entries(long nowTicks, long ticksPerSecond)
    {
        var entries = new List<FpsEntry>();
        foreach (var (pid, p) in _procs.ToList())
        {
            if (nowTicks - p.LastSeen > 5 * ticksPerSecond) { _procs.Remove(pid); continue; }
            if (nowTicks - p.LastSeen > 2 * ticksPerSecond) continue;
            int best = p.Chains.Values.Max(ch => ch.Times.Count);
            if (best < 2) continue;
            if (Ignored.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            entries.Add(new FpsEntry(pid, p.Name, best));
        }
        return entries;
    }
}
