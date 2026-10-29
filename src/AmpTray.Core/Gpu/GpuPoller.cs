using System;
using System.Threading;

namespace AmpTray.Gpu;

/// <summary>
/// Polls the first NVIDIA GPU through NVML on a background thread. Read-only.
/// If there is no NVIDIA driver, <see cref="Available"/> stays false and it quietly retries now and then.
/// </summary>
public sealed class GpuPoller : IDisposable
{
    private const int RetryDelayMs = 60_000;

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private volatile int _intervalMs;
    private volatile int _deviceIndex;
    private IntPtr _device;
    private bool _initialised;
    private string _name = "", _driver = "";
    private double _slowdownC = 90, _shutdownC = 100;
    private double _coreMaxMhz, _memMaxMhz, _powerMaxW;
    private int _fanCount;
    private int _pcieGen, _pcieWidth;
    private bool _memTempSupported = true, _energySupported = true, _encSupported = true, _eventReasonsSupported = true;

    public bool Available { get; private set; }
    public string? Unavailable { get; private set; }

    /// <summary>Raised on the polling thread with a fresh sample, or null once when the GPU goes away.</summary>
    public event Action<GpuSnapshot?>? Sample;

    public GpuPoller(int intervalMs, int deviceIndex = 0)
    {
        _intervalMs = intervalMs;
        _deviceIndex = deviceIndex;
        _thread = new Thread(Run) { IsBackground = true, Name = "GPU poller" };
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 250, 5000);
    }

    public int DeviceIndex
    {
        get => _deviceIndex;
        set => _deviceIndex = Math.Max(0, value);
    }

    public void Start() => _thread.Start();

    private readonly AutoResetEvent _wake = new(false);

    /// <summary>Re-open the GPU right away (resume from sleep) instead of waiting for the retry delay.</summary>
    public void Reconnect()
    {
        Available = false;
        _wake.Set();
    }

    private void Pause(int ms) => WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _wake }, ms);

    private void Run()
    {
        try { Loop(); }
        finally
        {
            // The thread owns the NVML session: shutting it down here can never race a call in progress.
            if (_initialised)
            {
                try { Nvml.Shutdown(); } catch { }
                _initialised = false;
            }
        }
    }

    private void Loop()
    {
        var ct = _cts.Token;
        int boundIndex = -1;
        while (!ct.IsCancellationRequested)
        {
            if (!Available || boundIndex != _deviceIndex)
            {
                if (!TryOpen(_deviceIndex))
                {
                    Pause(RetryDelayMs);
                    continue;
                }
                boundIndex = _deviceIndex;
            }

            GpuSnapshot? s = null;
            try { s = Read(); }
            catch (Exception ex) { Unavailable = ex.Message; }

            if (s == null)
            {
                Available = false;
                try { Sample?.Invoke(null); } catch { }
                Pause(RetryDelayMs);
                continue;
            }

            try { Sample?.Invoke(s); } catch { }
            Pause(_intervalMs);
        }
    }

    private bool TryOpen(int index)
    {
        try
        {
            if (!_initialised)
            {
                int r = Nvml.Init();
                if (r != Nvml.Success) { Unavailable = Nvml.Error(r); return false; }
                _initialised = true;
            }
            if (Nvml.GetCount(out uint count) != Nvml.Success || count == 0) { Unavailable = "No NVIDIA GPU"; return false; }
            uint idx = (uint)Math.Min(index, (int)count - 1);
            int rh = Nvml.GetHandleByIndex(idx, out _device);
            if (rh != Nvml.Success) { Unavailable = Nvml.Error(rh); return false; }

            var buf = new byte[96];
            _name = Nvml.GetName(_device, buf, (uint)buf.Length) == Nvml.Success ? Nvml.Ansi(buf) : "NVIDIA GPU";
            _driver = Nvml.GetDriverVersion(buf, (uint)buf.Length) == Nvml.Success ? Nvml.Ansi(buf) : "";
            if (Nvml.GetTemperatureThreshold(_device, Nvml.ThresholdSlowdown, out uint slow) == Nvml.Success && slow > 0) _slowdownC = slow;
            if (Nvml.GetTemperatureThreshold(_device, Nvml.ThresholdShutdown, out uint shut) == Nvml.Success && shut > 0) _shutdownC = shut;
            _coreMaxMhz = Nvml.GetMaxClockInfo(_device, Nvml.ClockGraphics, out uint cm) == Nvml.Success ? cm : 0;
            _memMaxMhz = Nvml.GetMaxClockInfo(_device, Nvml.ClockMem, out uint mm) == Nvml.Success ? mm : 0;
            _powerMaxW = Nvml.GetPowerLimitConstraints(_device, out _, out uint pmax) == Nvml.Success ? pmax / 1000.0 : 0;
            _fanCount = Nvml.GetNumFans(_device, out uint fans) == Nvml.Success ? (int)fans : 0;
            _pcieGen = Nvml.GetPcieGeneration(_device, out uint gen) == Nvml.Success ? (int)gen : 0;
            _pcieWidth = Nvml.GetPcieWidth(_device, out uint lanes) == Nvml.Success ? (int)lanes : 0;

            Available = true;
            Unavailable = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            Unavailable = $"NVIDIA driver ({Nvml.LibraryName}) not found";
            return false;
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            return false;
        }
    }

    private GpuSnapshot? Read()
    {
        if (Nvml.GetTemperature(_device, Nvml.TempGpu, out uint temp) != Nvml.Success) return null;

        Nvml.GetUtilizationRates(_device, out var util);
        Nvml.GetMemoryInfo(_device, out var mem);
        Nvml.GetPowerUsage(_device, out uint powerMw);
        Nvml.GetEnforcedPowerLimit(_device, out uint limitMw);
        Nvml.GetClockInfo(_device, Nvml.ClockGraphics, out uint core);
        Nvml.GetClockInfo(_device, Nvml.ClockMem, out uint memClk);
        Nvml.GetPerformanceState(_device, out int pstate);

        double? fan = null;
        if (_fanCount > 0 && Nvml.GetFanSpeed(_device, 0, out uint fanPct) == Nvml.Success) fan = fanPct;
        else if (Nvml.GetFanSpeedLegacy(_device, out uint legacy) == Nvml.Success) fan = legacy;

        ulong reasons = 0;
        if (_eventReasonsSupported)
        {
            try { if (Nvml.GetClocksEventReasons(_device, out reasons) != Nvml.Success) reasons = 0; }
            catch (EntryPointNotFoundException) { _eventReasonsSupported = false; }
        }
        if (!_eventReasonsSupported)
        {
            try { if (Nvml.GetClocksThrottleReasons(_device, out reasons) != Nvml.Success) reasons = 0; }
            catch (EntryPointNotFoundException) { }
        }

        double? memTemp = null;
        if (_memTempSupported)
        {
            try
            {
                var fields = new[] { new Nvml.FieldValue { FieldId = Nvml.FieldMemoryTemp } };
                if (Nvml.GetFieldValues(_device, 1, fields) == Nvml.Success && fields[0].Result == Nvml.Success)
                    memTemp = fields[0].ValueType == 0 ? BitConverter.Int64BitsToDouble((long)fields[0].Value) : (uint)fields[0].Value;
                else
                    _memTempSupported = false;
            }
            catch (EntryPointNotFoundException) { _memTempSupported = false; }
        }

        double? energy = null;
        if (_energySupported)
        {
            try
            {
                if (Nvml.GetTotalEnergy(_device, out ulong mJ) == Nvml.Success) energy = mJ / 1000.0;
                else _energySupported = false;
            }
            catch (EntryPointNotFoundException) { _energySupported = false; }
        }

        double? enc = null, dec = null;
        if (_encSupported)
        {
            try
            {
                if (Nvml.GetEncoderUtilization(_device, out uint e, out _) == Nvml.Success) enc = e;
                if (Nvml.GetDecoderUtilization(_device, out uint d, out _) == Nvml.Success) dec = d;
            }
            catch (EntryPointNotFoundException) { _encSupported = false; }
        }

        return new GpuSnapshot
        {
            Name = _name,
            DriverVersion = _driver,
            TempC = temp,
            MemTempC = memTemp is > 0 and < 200 ? memTemp : null,
            SlowdownC = _slowdownC,
            ShutdownC = _shutdownC,
            LoadPct = util.Gpu,
            MemControllerPct = util.Memory,
            EncoderPct = enc,
            DecoderPct = dec,
            MemUsedBytes = mem.Used,
            MemTotalBytes = mem.Total,
            PowerW = powerMw / 1000.0,
            PowerLimitW = limitMw / 1000.0,
            PowerLimitMaxW = _powerMaxW,
            EnergyJ = energy,
            CoreMhz = core,
            CoreMaxMhz = _coreMaxMhz,
            MemMhz = memClk,
            MemMaxMhz = _memMaxMhz,
            FanPct = fan,
            FanCount = _fanCount,
            PState = pstate,
            Throttle = (ThrottleReason)reasons,
            PcieGen = _pcieGen,
            PcieWidth = _pcieWidth,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive && !_thread.Join(5000)) return; // still inside NVML; leave the CTS for it
        _cts.Dispose();
    }
}
