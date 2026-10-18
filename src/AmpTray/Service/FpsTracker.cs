using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AmpTray.Service;

/// <summary>
/// Counts frame presents per process from the Windows graphics ETW providers (the same events
/// PresentMon uses) and publishes frames-per-second for the busiest processes to fps.json.
/// Needs an elevated process, so it lives in the background service. Read-only: it only listens.
///
/// A frame is one present on one swap chain. Browsers and Electron apps present several composition
/// surfaces per frame, so presents are counted per swap chain and a process is reported at the rate of
/// its busiest swap chain, which is the game's main one. Presents flagged DXGI_PRESENT_TEST are not frames.
/// </summary>
public sealed class FpsTracker : IDisposable
{
    private const string SessionName = "AmpTrayPresent";
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl, Present = 184
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");    // Microsoft-Windows-DXGI, Present_Start = 42
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");    // Microsoft-Windows-D3D9, Present_Start = 1
    private const int DxgKrnlPresent = 184;
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const uint DxgiPresentTest = 0x1;
    private static readonly string[] Ignored = { "dwm", "csrss", "AmpTray" };

    private sealed class PidStat
    {
        public readonly Dictionary<ulong, Queue<long>> SwapChains = new(); // user-mode presents per swap chain
        public readonly Queue<long> Kernel = new();                          // kernel presents (any API), fallback only
        public long LastSeen;
        public string? Name;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, PidStat> _pids = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private Timer? _publish;

    public bool Active { get; private set; }

    private int _intervalMs = 500;

    public void SetInterval(int ms)
    {
        _intervalMs = Math.Clamp(ms, 250, 5000);
        _publish?.Change(_intervalMs, _intervalMs);
    }

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
            // Only the three present events are delivered; the kernel graphics provider is very chatty otherwise.
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgKrnlPresent } });
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStart } });
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { D3D9PresentStart } });
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { ServiceSettings.Log("FPS trace stopped: " + ex.Message); }
            }) { IsBackground = true, Name = "FPS trace" };
            _thread.Start();
            _publish = new Timer(_ => Publish(), null, _intervalMs, _intervalMs);
            Active = true;
            ServiceSettings.Log("FPS tracker started");
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("FPS tracker failed to start: " + ex.Message);
            Dispose();
        }
    }

    private long _seenDxgi, _seenD3D9, _seenKernel, _seenTest;
    private long _lastDiag = Stopwatch.GetTimestamp();

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        ulong swapChain;
        bool kernel = false;
        if (e.ProviderGuid == Dxgi && id == DxgiPresentStart)
        {
            // Payload: pIDXGISwapChain (pointer), Flags (uint32), SyncInterval (int32). Read raw: the
            // manifest is not always available to the parser, and the pointer is 4 bytes for 32-bit games.
            var (sc, flags) = PointerAndFlags(e);
            if ((flags & DxgiPresentTest) != 0) { _seenTest++; return; } // a test present draws nothing
            swapChain = sc;
            _seenDxgi++;
        }
        else if (e.ProviderGuid == D3D9 && id == D3D9PresentStart)
        {
            swapChain = PointerAndFlags(e).Pointer; // pSwapchain, Flags
            _seenD3D9++;
        }
        else if (e.ProviderGuid == DxgKrnl && id == DxgKrnlPresent)
        {
            kernel = true;
            swapChain = 0;
            _seenKernel++;
        }
        else
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_pids.TryGetValue(e.ProcessID, out var st)) _pids[e.ProcessID] = st = new PidStat();
            if (kernel)
            {
                st.Kernel.Enqueue(now);
            }
            else
            {
                if (!st.SwapChains.TryGetValue(swapChain, out var q)) st.SwapChains[swapChain] = q = new Queue<long>();
                q.Enqueue(now);
            }
            st.LastSeen = now;
        }
    }

    private static (ulong Pointer, uint Flags) PointerAndFlags(TraceEvent e)
    {
        try
        {
            var data = e.EventData();
            int ps = e.PointerSize == 4 ? 4 : 8;
            if (data.Length < ps) return (0, 0);
            ulong ptr = ps == 4 ? BitConverter.ToUInt32(data, 0) : BitConverter.ToUInt64(data, 0);
            uint flags = data.Length >= ps + 4 ? BitConverter.ToUInt32(data, ps) : 0u;
            return (ptr, flags);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Frames in the last second per process (busiest swap chain), busiest first.</summary>
    private void Publish()
    {
        long now = Stopwatch.GetTimestamp();
        long second = Stopwatch.Frequency;
        var entries = new List<FpsEntry>();
        lock (_gate)
        {
            foreach (var (pid, st) in _pids.ToList())
            {
                if (now - st.LastSeen > 5 * second) { _pids.Remove(pid); continue; }

                int best = 0;
                foreach (var (sc, q) in st.SwapChains.ToList())
                {
                    Trim(q, now - second);
                    if (q.Count == 0 && now - st.LastSeen > second) st.SwapChains.Remove(sc);
                    best = Math.Max(best, q.Count);
                }
                Trim(st.Kernel, now - second);
                // No user-mode present events (Vulkan/OpenGL paths): fall back to kernel presents.
                if (st.SwapChains.Count == 0) best = st.Kernel.Count;

                if (best < 2) continue;
                st.Name ??= NameOf(pid);
                if (Ignored.Contains(st.Name, StringComparer.OrdinalIgnoreCase)) continue;
                entries.Add(new FpsEntry(pid, st.Name, best));
            }
        }
        new FpsSnapshot { Timestamp = DateTime.Now, Processes = entries.OrderByDescending(e => e.Fps).Take(8).ToList() }.TrySave();

        // Once a minute: how many present events arrived per source (helps when a game shows no FPS).
        if (now - _lastDiag > 60 * second)
        {
            _lastDiag = now;
            ServiceSettings.Log($"FPS trace: dxgi={_seenDxgi} d3d9={_seenD3D9} kernel={_seenKernel} test={_seenTest} processes={entries.Count}" +
                (entries.Count > 0 ? " top=" + string.Join(", ", entries.OrderByDescending(e => e.Fps).Take(3).Select(e => $"{e.Name}:{e.Fps:0}")) : ""));
            _seenDxgi = _seenD3D9 = _seenKernel = _seenTest = 0;
        }
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
