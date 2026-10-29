using System;
using Avalonia.Threading;

namespace AmpTray.Avalonia;

/// <summary>
/// Notices sleep/resume and clock changes without a system hook: every second it compares the wall
/// clock with the monotonic tick count (which does not advance during suspend). A jump of more than
/// 30 s between the two means the PC slept or the clock was set, either of which makes elapsed
/// wall-clock time meaningless for the shutdown rules.
/// </summary>
public sealed class WakeDetector : IDisposable
{
    private const double ThresholdSeconds = 30;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastWall = DateTime.UtcNow;
    private long _lastTick = Environment.TickCount64;

    /// <summary>Raised on the UI thread with a short reason.</summary>
    public event Action<string>? Jumped;

    public WakeDetector()
    {
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        var wall = DateTime.UtcNow;
        long tick = Environment.TickCount64;
        double wallDelta = (wall - _lastWall).TotalSeconds;
        double monoDelta = (tick - _lastTick) / 1000.0;
        _lastWall = wall;
        _lastTick = tick;

        double diff = wallDelta - monoDelta;
        if (Math.Abs(diff) < ThresholdSeconds) return;
        Jumped?.Invoke(diff > 0 ? "resumed from sleep or clock set forward" : "clock set back");
    }

    public void Dispose() => _timer.Stop();
}
