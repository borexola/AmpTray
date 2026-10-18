using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AmpTray.Service;

/// <summary>
/// Counts frame presents per process from the kernel graphics ETW providers (the same events
/// PresentMon uses) and publishes presents-per-second for the busiest processes to fps.json.
/// Needs an elevated process, so it lives in the background service. Read-only: it only listens.
/// </summary>
public sealed class FpsTracker : IDisposable
{
    private const string SessionName = "AmpTrayPresent";
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl, Present = 184
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");    // Microsoft-Windows-DXGI, Present_Start = 42
    private const int DxgKrnlPresent = 184;
    private const int DxgiPresentStart = 42;
    private static readonly string[] Ignored = { "dwm", "csrss", "AmpTray", "explorer", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost" };

    private sealed class PidStat
    {
        public readonly Queue<long> Kernel = new();
        public readonly Queue<long> Dxgi = new();
        public long LastSeen;
        public string? Name;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, PidStat> _pids = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private Timer? _publish;

    public bool Active { get; private set; }

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
        {
            ServiceSettings.Log("FPS tracker not started: process is not elevated");
            return;
        }
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, 0x1 /* Base keyword carries Present */);
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { ServiceSettings.Log("FPS trace stopped: " + ex.Message); }
            }) { IsBackground = true, Name = "FPS trace" };
            _thread.Start();
            _publish = new Timer(_ => Publish(), null, 500, 500);
            Active = true;
            ServiceSettings.Log("FPS tracker started");
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("FPS tracker failed to start: " + ex.Message);
            Dispose();
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool kernel = e.ProviderGuid == DxgKrnl && id == DxgKrnlPresent;
        bool dxgi = e.ProviderGuid == Dxgi && id == DxgiPresentStart;
        if (!kernel && !dxgi) return;

        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_pids.TryGetValue(e.ProcessID, out var st)) _pids[e.ProcessID] = st = new PidStat();
            (kernel ? st.Kernel : st.Dxgi).Enqueue(now);
            st.LastSeen = now;
        }
    }

    /// <summary>Presents in the last second per process, busiest first, excluding the desktop plumbing.</summary>
    private void Publish()
    {
        long now = Stopwatch.GetTimestamp();
        long second = Stopwatch.Frequency;
        var entries = new List<FpsEntry>();
        lock (_gate)
        {
            foreach (var (pid, st) in _pids.ToList())
            {
                Trim(st.Kernel, now - second);
                Trim(st.Dxgi, now - second);
                if (now - st.LastSeen > 5 * second) { _pids.Remove(pid); continue; }
                // Kernel presents cover every API; fall back to DXGI user-mode presents if the kernel event is missing.
                int count = st.Kernel.Count > 0 ? st.Kernel.Count : st.Dxgi.Count;
                if (count < 2) continue;
                st.Name ??= NameOf(pid);
                if (Ignored.Contains(st.Name, StringComparer.OrdinalIgnoreCase)) continue;
                entries.Add(new FpsEntry(pid, st.Name, count));
            }
        }
        new FpsSnapshot { Timestamp = DateTime.Now, Processes = entries.OrderByDescending(e => e.Fps).Take(8).ToList() }.TrySave();
    }

    private static void Trim(Queue<long> q, long olderThan)
    {
        while (q.Count > 0 && q.Peek() < olderThan) q.Dequeue();
    }

    private static string NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return $"pid {pid}"; }
    }

    public void Dispose()
    {
        Active = false;
        _publish?.Dispose();
        _publish = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        if (_thread != null && _thread.IsAlive) _thread.Join(3000);
        _thread = null;
    }
}
