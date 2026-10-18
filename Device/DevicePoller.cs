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
    private readonly Thread _thread;
    private volatile int _intervalMs;

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
                if (!_reader.TryConnect())
                {
                    ct.WaitHandle.WaitOne(RescanDelayMs);
                    continue;
                }
                Raise(() => ConnectionChanged?.Invoke(_reader.PortName));

                int failures = 0;
                while (!ct.IsCancellationRequested && failures < FailuresBeforeRescan)
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
                    ct.WaitHandle.WaitOne(_intervalMs);
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
        if (_thread.IsAlive && !_thread.Join(5000)) return; // still inside a serial call; leave the CTS for it
        _cts.Dispose();
    }
}
