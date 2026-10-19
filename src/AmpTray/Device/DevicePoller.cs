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

    /// <summary>Drop the current port and look for the device again right away (resume from sleep, USB reset).</summary>
    public void Reconnect()
    {
        _reader.ForgetRejections();
        _rescan = true;
        _wake.Set();
    }

    /// <summary>Waits for the interval, cancellation or a Reconnect() request.</summary>
    private void Pause(int ms)
    {
        WaitHandle.WaitAny(new[] { _cts.Token.WaitHandle, _wake }, ms);
    }

    /// <summary>Raised on the polling thread with a fresh sample.</summary>
    public event Action<SensorSnapshot>? Sample;

    /// <summary>Raised on the polling thread; argument is the COM port, or null when disconnected.</summary>
    public event Action<string?>? ConnectionChanged;

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
                    Pause(RescanDelayMs);
                    continue;
                }
                Raise(() => ConnectionChanged?.Invoke(_reader.PortName));

                int failures = 0;
                while (!ct.IsCancellationRequested && failures < FailuresBeforeRescan && !_rescan)
                {
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
