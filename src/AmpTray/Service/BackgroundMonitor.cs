using System;
using System.IO;
using System.Linq;
using System.Threading;
using AmpTray.Device;
using AmpTray.Gpu;

namespace AmpTray.Service;

/// <summary>
/// Headless version of what the widget does for protection: poll the device and the GPU, log to CSV
/// and evaluate the auto-shutdown rules. Runs inside the Windows service, so it works from boot with
/// nobody signed in. It never touches the device beyond the same two read-only requests.
/// </summary>
public sealed class BackgroundMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly ShutdownMonitor _rules = new();
    private readonly DataLogger _logger = new();
    private readonly FpsTracker _fps = new();
    private readonly Timer _reload;

    private ServiceSettings _cfg = ServiceSettings.Load();
    private DateTime _cfgStamp;
    private DevicePoller? _device;
    private GpuPoller? _gpu;
    private SensorSnapshot? _last;
    private GpuSnapshot? _lastGpu;
    private bool _issued;
    private DateTime _retryNotBefore = DateTime.MinValue;
    private string _pendingSummary = "";

    public BackgroundMonitor()
    {
        _reload = new Timer(_ => ReloadIfChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _cfgStamp = Stamp();
        ServiceSettings.Log($"Monitor starting (poll {_cfg.PollMs} ms, armed={_cfg.Shutdown.Enabled}, logging={_cfg.LogEnabled})");

        _device = new DevicePoller(_cfg.PollMs);
        _device.ConnectionChanged += port => { ServiceSettings.Log(port == null ? "Device disconnected" : $"Device connected on {port}"); if (port == null) OnDisconnected(); };
        _device.Sample += OnSample;
        _gpu = new GpuPoller(_cfg.PollMs, _cfg.GpuIndex);
        _gpu.Sample += OnGpu;
        _rules.MaxGap = TimeSpan.FromSeconds(Math.Max(10, _cfg.PollMs * 4 / 1000.0));

        _device.Start();
        _gpu.Start();
        _fps.Start();
        _reload.Change(5000, 5000);
    }

    private void OnSample(SensorSnapshot s)
    {
        lock (_gate)
        {
            _last = s;
            if (_cfg.LogEnabled) _logger.Write(s, _lastGpu, _cfg.LogFolder, _cfg.LogIntervalSeconds);
            Evaluate(deviceFresh: true, gpuFresh: false);
        }
    }

    private void OnGpu(GpuSnapshot? g)
    {
        lock (_gate)
        {
            _lastGpu = g;
            Evaluate(deviceFresh: false, gpuFresh: g != null);
        }
    }

    private void OnDisconnected()
    {
        lock (_gate)
        {
            _last = null;
            Evaluate(deviceFresh: false, gpuFresh: false);
        }
    }

    private void Evaluate(bool deviceFresh, bool gpuFresh)
    {
        var now = DateTime.Now;
        var gpu = _lastGpu != null && (now - _lastGpu.Timestamp).TotalSeconds < 10 ? _lastGpu : null;
        var result = _rules.Evaluate(_last, gpu, now, _cfg.Shutdown, deviceFresh, gpuFresh);

        // Log a rule starting or stopping to count, but not every tick.
        string summary = string.Join("; ", result.Pending.Select(p => p.Condition));
        if (summary != _pendingSummary)
        {
            ServiceSettings.Log(summary.Length == 0 ? "No rule pending" : "Pending: " + summary);
            _pendingSummary = summary;
        }

        if (result.Triggered is PendingRule p && !_issued && now >= _retryNotBefore)
        {
            string reason = $"{ShutdownMonitor.Describe(p.Rule, _cfg.Shutdown)}: {p.Condition}";
            _issued = true;
            ServiceSettings.Log("Shutting down (service): " + reason);
            PcShutdown.Execute(reason, _cfg.LogFolder, _cfg.Shutdown.CountdownSeconds, message =>
            {
                lock (_gate)
                {
                    _issued = false;
                    _retryNotBefore = DateTime.Now.AddSeconds(60);
                }
                ServiceSettings.Log("Shutdown failed: " + message + " (retry in 60 s)");
            });
        }
    }

    private static DateTime Stamp()
    {
        try { return File.Exists(ServiceSettings.FilePath) ? File.GetLastWriteTimeUtc(ServiceSettings.FilePath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Picks up settings the widget saved; rule timers are kept, pollers adopt the new rate.</summary>
    private void ReloadIfChanged()
    {
        var stamp = Stamp();
        if (stamp == _cfgStamp) return;
        lock (_gate)
        {
            _cfgStamp = stamp;
            _cfg = ServiceSettings.Load();
            if (_device != null) _device.IntervalMs = _cfg.PollMs;
            if (_gpu != null) { _gpu.IntervalMs = _cfg.PollMs; _gpu.DeviceIndex = _cfg.GpuIndex; }
            _rules.MaxGap = TimeSpan.FromSeconds(Math.Max(10, _cfg.PollMs * 4 / 1000.0));
            if (!_cfg.LogEnabled) _logger.Close();
            ServiceSettings.Log($"Settings reloaded (poll {_cfg.PollMs} ms, armed={_cfg.Shutdown.Enabled}, logging={_cfg.LogEnabled})");
        }
    }

    public void Dispose()
    {
        _reload.Dispose();
        _fps.Dispose();
        _device?.Dispose();
        _gpu?.Dispose();
        _logger.Dispose();
        ServiceSettings.Log("Monitor stopped");
    }

    /// <summary>Runs the monitor in this process for a while (developer check: AmpTray.exe /monitor:30).</summary>
    public static int RunForSeconds(int seconds)
    {
        using var m = new BackgroundMonitor();
        m.Start();
        Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600)));
        return 0;
    }
}
