using System;
using System.Threading;

namespace AmpTray.Device;

/// <summary>Background loop: find the device, poll it, rescan after repeated failures.</summary>
public sealed class DevicePoller : IDisposable
{
    private const int RescanDelayMs = 3000;
    private const int FailuresBeforeRescan = 3;

    private readonly WireViewReader _reader = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile int _intervalMs;
    private volatile bool _rescan;
    private volatile bool _clearFaults;

    /// <summary>Drop the current port and look for the device again right away (resume from sleep, USB reset).</summary>
    public void Reconnect()
    {
        _reader.ForgetRejections();
        _rescan = true;
        _wake.Set();
    }

    /// <summary>
    /// Asks the device to clear its faults, unless one it is set to power the PC off for is active
    /// (see <see cref="WireViewReader.ClearFaults"/>). Done on the polling thread before its next
    /// read, so the following sample already shows the result. <see cref="ClearFaultsDone"/> reports
    /// how it ended; a request made while the device is gone is reported as not sent.
    /// </summary>
    public void ClearFaults()
    {
        _clearFaults = true;
        _wake.Set();
    }

    /// <summary>Waits for the interval, cancellation, or a Reconnect() or ClearFaults() request.</summary>
    private void Pause(int ms)
    {
        WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _wake }, ms);
    }

    /// <summary>Raised on the polling thread with a fresh sample.</summary>
    public event Action<SensorSnapshot>? Sample;

    /// <summary>Raised on the polling thread; argument is the COM port, or null when disconnected.</summary>
    public event Action<string?>? ConnectionChanged;

    /// <summary>Raised on the polling thread after a ClearFaults() request, with how it ended.</summary>
    public event Action<ClearFaultsResult>? ClearFaultsDone;

    public DevicePoller(int intervalMs)
    {
        _intervalMs = intervalMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "WireView poller" };
    }

    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Clamp(value, 250, 5000);
    }

    public string? MutexProblem => _reader.MutexProblem;

    public void Start() => _thread.Start();

    private void Run()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _rescan = false;
                if (!_reader.TryConnect())
                {
                    if (_clearFaults)
                    {
                        // A request made for a device that has gone must not fire on a later one.
                        _clearFaults = false;
                        Raise(() => ClearFaultsDone?.Invoke(new(ClearFaultsOutcome.NotSent, 0)));
                    }
                    Pause(RescanDelayMs);
                    continue;
                }
                Raise(() => ConnectionChanged?.Invoke(_reader.PortName));

                int failures = 0;
                while (!ct.IsCancellationRequested && failures < FailuresBeforeRescan && !_rescan)
                {
                    if (_clearFaults)
                    {
                        _clearFaults = false;
                        var result = new ClearFaultsResult(ClearFaultsOutcome.NotSent, 0);
                        try { result = _reader.ClearFaults(); }
                        catch { /* unplugged or port grabbed; reported as not sent */ }
                        Raise(() => ClearFaultsDone?.Invoke(result));
                    }

                    SensorSnapshot? s = null;
                    try { s = _reader.ReadSensors(); }
                    catch { /* unplugged or port grabbed; counted below */ }

                    if (s != null)
                    {
                        failures = 0;
                        Raise(() => Sample?.Invoke(s));
                    }
                    else
                    {
                        failures++;
                    }
                    Pause(_intervalMs);
                }
                Raise(() => ConnectionChanged?.Invoke(null));
            }
        }
        finally
        {
            // The thread owns the reader: disposing it here can never race a read in progress.
            _reader.Dispose();
        }
    }

    private static void Raise(Action a)
    {
        try { a(); }
        catch { /* a subscriber fault must not kill the polling thread */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(5000)) return; // still inside a serial call; leave the handles for it
        _cts.Dispose();
        _wake.Dispose();
    }
}
